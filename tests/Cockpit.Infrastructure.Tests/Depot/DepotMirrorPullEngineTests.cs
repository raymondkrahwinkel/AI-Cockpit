using Cockpit.Core.Abstractions.Depot;
using Cockpit.Core.Depot;
using Cockpit.Infrastructure.Depot;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Depot;

/// <summary>
/// One counter-example per AC-281 acceptance criterion 1-4: base bytes plus a full index entry (1), diffing on
/// listing checksums with no leftover partial write (2), a failed round leaving its base/index pair untouched
/// (3), and a both-sides-changed file confirmed only via Depot's version history (4). Criterion 5 holds by
/// construction — <see cref="IDepotSyncClient"/> exposes no <c>restore_version</c> method to call. Plus one
/// review fix: a file Depot no longer has is retained, not deleted, when its working copy has itself diverged.
/// </summary>
public sealed class DepotMirrorPullEngineTests : IDisposable
{
    private readonly string _mirrorPath = Path.Combine(Path.GetTempPath(), $"cockpit-depotpull-{Guid.NewGuid():n}");
    private const string ServerName = "Depot: Work";
    private const string Project = "acme";

    [Fact]
    public async Task PullAsync_ANewFile_GetsLocalBaseBytesAndAFullIndexEntry()
    {
        var client = Substitute.For<IDepotSyncClient>();
        client.ListAllAsync(ServerName, Project, null, Arg.Any<CancellationToken>())
            .Returns(DepotListResult.Success([new DepotFileEntry("notes/a.md", 5, DateTimeOffset.UtcNow, "sha-a")]));
        client.ReadManyAsync(ServerName, Project, Arg.Is<IReadOnlyList<string>>(p => p.SequenceEqual(new[] { "notes/a.md" })), Arg.Any<CancellationToken>())
            .Returns(DepotReadManyResult.Success([new DepotReadFile("notes/a.md", "hello", "sha-a")], [], []));
        var engine = new DepotMirrorPullEngine(client);

        var result = await engine.PullAsync(_Mirror(), ServerName, Project);

        Assert.Equal(DepotPullOutcome.Success, result.Outcome);
        Assert.Equal(["notes/a.md"], result.Pulled);
        var workingFile = Path.Combine(_mirrorPath, "notes", "a.md");
        var baseFile = Path.Combine(ShadowSyncStorage.BaseRoot(_mirrorPath), "notes", "a.md");
        Assert.Equal("hello", File.ReadAllText(workingFile));
        Assert.Equal("hello", File.ReadAllText(baseFile));

        var entry = ShadowSyncStorage.LoadIndex(_mirrorPath)!["notes/a.md"];
        Assert.Equal("sha-a", entry.BaseChecksum);
        Assert.Equal(new FileInfo(workingFile).Length, entry.Size);
        Assert.Equal(new FileInfo(workingFile).LastWriteTimeUtc, entry.Mtime);
    }

    [Fact]
    public async Task PullAsync_DiffsOnListingChecksums_AndLeavesNoPartialWriteBehind()
    {
        Directory.CreateDirectory(_mirrorPath);
        File.WriteAllText(Path.Combine(_mirrorPath, "b.md"), "same");
        File.WriteAllText(Path.Combine(_mirrorPath, "c.md"), "old");
        var cInfo = new FileInfo(Path.Combine(_mirrorPath, "c.md"));
        var bInfo = new FileInfo(Path.Combine(_mirrorPath, "b.md"));
        ShadowSyncStorage.SaveIndex(_mirrorPath, new Dictionary<string, ShadowIndexEntry>
        {
            ["b.md"] = new ShadowIndexEntry("b.md", "sha-b", bInfo.Length, bInfo.LastWriteTimeUtc),
            ["c.md"] = new ShadowIndexEntry("c.md", "sha-c-old", cInfo.Length, cInfo.LastWriteTimeUtc),
        });

        var client = Substitute.For<IDepotSyncClient>();
        client.ListAllAsync(ServerName, Project, null, Arg.Any<CancellationToken>())
            .Returns(DepotListResult.Success([
                new DepotFileEntry("b.md", 4, DateTimeOffset.UtcNow, "sha-b"),
                new DepotFileEntry("c.md", 3, DateTimeOffset.UtcNow, "sha-c-new"),
            ]));
        client.ReadManyAsync(ServerName, Project, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(DepotReadManyResult.Success([new DepotReadFile("c.md", "new", "sha-c-new")], [], []));
        var engine = new DepotMirrorPullEngine(client);

        await engine.PullAsync(_Mirror(), ServerName, Project);

        await client.Received(1).ReadManyAsync(
            ServerName, Project, Arg.Is<IReadOnlyList<string>>(p => p.SequenceEqual(new[] { "c.md" })), Arg.Any<CancellationToken>());
        Assert.Equal("new", File.ReadAllText(Path.Combine(_mirrorPath, "c.md")));
        Assert.Empty(Directory.GetFiles(_mirrorPath, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task PullAsync_AFileWhoseReadFailsOutright_KeepsItsEarlierBaseAndIndexPairUntouched()
    {
        Directory.CreateDirectory(_mirrorPath);
        File.WriteAllText(Path.Combine(_mirrorPath, "stale.md"), "old-content");
        var staleInfo = new FileInfo(Path.Combine(_mirrorPath, "stale.md"));
        var oldEntry = new ShadowIndexEntry("stale.md", "sha-old", staleInfo.Length, staleInfo.LastWriteTimeUtc);
        ShadowSyncStorage.WriteBaseFile(_mirrorPath, "stale.md", "old-content");
        ShadowSyncStorage.SaveIndex(_mirrorPath, new Dictionary<string, ShadowIndexEntry> { ["stale.md"] = oldEntry });

        var client = Substitute.For<IDepotSyncClient>();
        client.ListAllAsync(ServerName, Project, null, Arg.Any<CancellationToken>())
            .Returns(DepotListResult.Success([
                new DepotFileEntry("stale.md", 20, DateTimeOffset.UtcNow, "sha-new"),
                new DepotFileEntry("fresh.md", 5, DateTimeOffset.UtcNow, "sha-fresh"),
            ]));
        client.ReadManyAsync(ServerName, Project, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(DepotReadManyResult.Success([new DepotReadFile("fresh.md", "fresh", "sha-fresh")], [], ["stale.md"]));
        var engine = new DepotMirrorPullEngine(client);

        var result = await engine.PullAsync(_Mirror(), ServerName, Project);

        Assert.Equal(["stale.md"], result.Unreadable);
        Assert.Equal(["fresh.md"], result.Pulled);
        Assert.Equal("old-content", File.ReadAllText(Path.Combine(_mirrorPath, "stale.md")));
        Assert.Equal(oldEntry, ShadowSyncStorage.LoadIndex(_mirrorPath)!["stale.md"]);
    }

    [Fact]
    public async Task PullAsync_ADivergedFile_IsConfirmedOnlyWhenItsBaseStillTurnsUpInVersionHistory_AndIsNeverTouched()
    {
        Directory.CreateDirectory(_mirrorPath);
        File.WriteAllText(Path.Combine(_mirrorPath, "confirmed.md"), "local-edit-confirmed");
        File.WriteAllText(Path.Combine(_mirrorPath, "unconfirmed.md"), "local-edit-unconfirmed");

        // Recorded stats predate today's edit above — the working copy has diverged from what was last synced.
        var oldEntry = new ShadowIndexEntry("confirmed.md", "sha-base", 999, DateTimeOffset.UtcNow.AddDays(-1));
        ShadowSyncStorage.SaveIndex(_mirrorPath, new Dictionary<string, ShadowIndexEntry>
        {
            ["confirmed.md"] = oldEntry,
            ["unconfirmed.md"] = new ShadowIndexEntry("unconfirmed.md", "sha-base", 999, DateTimeOffset.UtcNow.AddDays(-1)),
        });

        var client = Substitute.For<IDepotSyncClient>();
        client.ListAllAsync(ServerName, Project, null, Arg.Any<CancellationToken>())
            .Returns(DepotListResult.Success([
                new DepotFileEntry("confirmed.md", 1, DateTimeOffset.UtcNow, "sha-remote-new"),
                new DepotFileEntry("unconfirmed.md", 1, DateTimeOffset.UtcNow, "sha-remote-new"),
            ]));
        client.ListVersionsAsync(ServerName, Project, "confirmed.md", Arg.Any<CancellationToken>())
            .Returns(DepotListVersionsResult.Success([new DepotFileVersion("v1", DateTimeOffset.UtcNow, 10, "sha-base")]));
        client.ListVersionsAsync(ServerName, Project, "unconfirmed.md", Arg.Any<CancellationToken>())
            .Returns(DepotListVersionsResult.Success([new DepotFileVersion("v1", DateTimeOffset.UtcNow, 10, "sha-something-else")]));
        var engine = new DepotMirrorPullEngine(client);

        var result = await engine.PullAsync(_Mirror(), ServerName, Project);

        Assert.True(result.Diverged.Single(d => d.Path == "confirmed.md").BaseConfirmed);
        Assert.False(result.Diverged.Single(d => d.Path == "unconfirmed.md").BaseConfirmed);
        Assert.Empty(result.Pulled);
        Assert.Equal("local-edit-confirmed", File.ReadAllText(Path.Combine(_mirrorPath, "confirmed.md")));
        Assert.Equal("local-edit-unconfirmed", File.ReadAllText(Path.Combine(_mirrorPath, "unconfirmed.md")));
        Assert.Equal(oldEntry, ShadowSyncStorage.LoadIndex(_mirrorPath)!["confirmed.md"]);
        await client.DidNotReceiveWithAnyArgs().ReadManyAsync(default!, default!, default!);
    }

    [Fact]
    public async Task PullAsync_ADivergedWorkingCopy_IsRetainedRatherThanDeleted_WhenDepotNoLongerHasTheFile()
    {
        Directory.CreateDirectory(_mirrorPath);

        // Scenario A: Depot's listing no longer mentions this path at all.
        File.WriteAllText(Path.Combine(_mirrorPath, "gone-from-listing.md"), "local edit, never pushed");
        var goneEntry = new ShadowIndexEntry("gone-from-listing.md", "sha-old", 999, DateTimeOffset.UtcNow.AddDays(-1));

        // Scenario B: Depot's listing still shows it as changed, but the operator edits the local copy in the
        // window between the listing diff and read_many reporting it Missing.
        File.WriteAllText(Path.Combine(_mirrorPath, "deleted-mid-read.md"), "not yet edited");
        var midReadInfo = new FileInfo(Path.Combine(_mirrorPath, "deleted-mid-read.md"));
        var midReadEntry = new ShadowIndexEntry("deleted-mid-read.md", "sha-old", midReadInfo.Length, midReadInfo.LastWriteTimeUtc);

        ShadowSyncStorage.SaveIndex(_mirrorPath, new Dictionary<string, ShadowIndexEntry>
        {
            ["gone-from-listing.md"] = goneEntry,
            ["deleted-mid-read.md"] = midReadEntry,
        });

        var client = Substitute.For<IDepotSyncClient>();
        client.ListAllAsync(ServerName, Project, null, Arg.Any<CancellationToken>())
            .Returns(DepotListResult.Success([new DepotFileEntry("deleted-mid-read.md", 1, DateTimeOffset.UtcNow, "sha-new")]));
        client.ReadManyAsync(ServerName, Project, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                File.WriteAllText(Path.Combine(_mirrorPath, "deleted-mid-read.md"), "edited during the race");
                return DepotReadManyResult.Success([], ["deleted-mid-read.md"], []);
            });
        var engine = new DepotMirrorPullEngine(client);

        var result = await engine.PullAsync(_Mirror(), ServerName, Project);

        Assert.Equal(
            new[] { "deleted-mid-read.md", "gone-from-listing.md" },
            result.Retained.OrderBy(path => path, StringComparer.Ordinal));
        Assert.Empty(result.Deleted);
        Assert.Equal("local edit, never pushed", File.ReadAllText(Path.Combine(_mirrorPath, "gone-from-listing.md")));
        Assert.Equal("edited during the race", File.ReadAllText(Path.Combine(_mirrorPath, "deleted-mid-read.md")));
        var indexAfter = ShadowSyncStorage.LoadIndex(_mirrorPath)!;
        Assert.Equal(goneEntry, indexAfter["gone-from-listing.md"]);
        Assert.Equal(midReadEntry, indexAfter["deleted-mid-read.md"]);
    }

    private DepotMirror _Mirror() => new("depot.example.com", Project, _mirrorPath, DateTimeOffset.UtcNow);

    public void Dispose()
    {
        if (Directory.Exists(_mirrorPath))
        {
            Directory.Delete(_mirrorPath, recursive: true);
        }
    }
}
