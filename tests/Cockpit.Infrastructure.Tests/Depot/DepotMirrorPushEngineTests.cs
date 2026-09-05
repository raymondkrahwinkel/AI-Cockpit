using Cockpit.Core.Abstractions.Depot;
using Cockpit.Core.Depot;
using Cockpit.Infrastructure.Depot;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Depot;

/// <summary>
/// One counter-example per AC-282 acceptance criterion 1-5, plus two extra tests for the loss-of-work paths the
/// ticket calls out by name: a file the pull engine left <c>Diverged</c> (test D) and one it left
/// <c>Retained</c> after Depot deleted it remotely (test E). Both push exactly like any other changed file — it
/// is <c>write_many</c>'s own conflict on the stale baseChecksum, not a special case in this engine, that stops
/// either from being silently pushed.
/// </summary>
public sealed class DepotMirrorPushEngineTests : IDisposable
{
    private readonly string _mirrorPath = Path.Combine(Path.GetTempPath(), $"cockpit-depotpush-{Guid.NewGuid():n}");
    private const string ServerName = "Depot: Work";
    private const string Project = "acme";

    [Fact]
    public async Task PushAsync_DetectsLocalChangesFromTheShadowIndex_AndConfirmsDoubtfulStatMismatchesWithContent()
    {
        Directory.CreateDirectory(_mirrorPath);

        File.WriteAllText(Path.Combine(_mirrorPath, "new.md"), "brand new");

        File.WriteAllText(Path.Combine(_mirrorPath, "same.md"), "unchanged");
        var sameInfo = new FileInfo(Path.Combine(_mirrorPath, "same.md"));

        // Stat says changed (a stale recorded mtime) but the content is identical to the base copy — the
        // doubtful case criterion 1 asks to confirm rather than push on the stat mismatch alone.
        File.WriteAllText(Path.Combine(_mirrorPath, "touched.md"), "unchanged body");
        ShadowSyncStorage.WriteBaseFile(_mirrorPath, "touched.md", "unchanged body");

        ShadowSyncStorage.SaveIndex(_mirrorPath, new Dictionary<string, ShadowIndexEntry>
        {
            ["same.md"] = new ShadowIndexEntry("same.md", "sha-same", sameInfo.Length, sameInfo.LastWriteTimeUtc),
            ["touched.md"] = new ShadowIndexEntry("touched.md", "sha-touched", 999, DateTimeOffset.UtcNow.AddDays(-1)),
        });

        var client = Substitute.For<IDepotSyncClient>();
        client.WriteManyAsync(ServerName, Project, Arg.Any<IReadOnlyList<DepotWriteEntry>>(), Arg.Any<CancellationToken>())
            .Returns(new DepotWriteManyResult([new DepotWriteEntryResult("new.md", DepotWriteStatus.Written, "sha-new", null)]));
        var engine = new DepotMirrorPushEngine(client);

        await engine.PushAsync(_Mirror(), ServerName, Project);

        await client.Received(1).WriteManyAsync(
            ServerName, Project,
            Arg.Is<IReadOnlyList<DepotWriteEntry>>(entries => entries.Select(e => e.Path).SequenceEqual(new[] { "new.md" })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PushAsync_EveryEntryCarriesExactlyItsStoredBaseChecksum()
    {
        Directory.CreateDirectory(_mirrorPath);
        File.WriteAllText(Path.Combine(_mirrorPath, "existing.md"), "changed body");
        ShadowSyncStorage.WriteBaseFile(_mirrorPath, "existing.md", "original body");
        ShadowSyncStorage.SaveIndex(_mirrorPath, new Dictionary<string, ShadowIndexEntry>
        {
            ["existing.md"] = new ShadowIndexEntry("existing.md", "sha-existing", 999, DateTimeOffset.UtcNow.AddDays(-1)),
        });
        File.WriteAllText(Path.Combine(_mirrorPath, "new.md"), "brand new");

        IReadOnlyList<DepotWriteEntry>? captured = null;
        var client = Substitute.For<IDepotSyncClient>();
        client.WriteManyAsync(ServerName, Project, Arg.Do<IReadOnlyList<DepotWriteEntry>>(e => captured = e), Arg.Any<CancellationToken>())
            .Returns(new DepotWriteManyResult([
                new DepotWriteEntryResult("existing.md", DepotWriteStatus.Written, "sha-existing-2", null),
                new DepotWriteEntryResult("new.md", DepotWriteStatus.Written, "sha-new", null),
            ]));
        var engine = new DepotMirrorPushEngine(client);

        await engine.PushAsync(_Mirror(), ServerName, Project);

        Assert.Equal("sha-existing", captured!.Single(e => e.Path == "existing.md").BaseChecksum);
        Assert.Null(captured!.Single(e => e.Path == "new.md").BaseChecksum);
    }

    [Fact]
    public async Task PushAsync_HandlesWrittenConflictAndInvalid_PerFile_WithoutBlockingIndependentFiles()
    {
        Directory.CreateDirectory(_mirrorPath);
        File.WriteAllText(Path.Combine(_mirrorPath, "a.md"), "a-new");
        File.WriteAllText(Path.Combine(_mirrorPath, "b.md"), "b-new");
        File.WriteAllText(Path.Combine(_mirrorPath, "c.md"), "c-new");

        var client = Substitute.For<IDepotSyncClient>();
        client.WriteManyAsync(ServerName, Project, Arg.Any<IReadOnlyList<DepotWriteEntry>>(), Arg.Any<CancellationToken>())
            .Returns(new DepotWriteManyResult([
                new DepotWriteEntryResult("a.md", DepotWriteStatus.Written, "sha-a", null),
                new DepotWriteEntryResult("b.md", DepotWriteStatus.Conflict, null, "changed since it was read"),
                new DepotWriteEntryResult("c.md", DepotWriteStatus.Invalid, null, "bad path"),
            ]));
        var engine = new DepotMirrorPushEngine(client);

        var result = await engine.PushAsync(_Mirror(), ServerName, Project);

        Assert.Equal(["a.md"], result.Pushed);
        Assert.Equal(["b.md"], result.Conflicted);
        Assert.Equal(["c.md"], result.Invalid);
        var index = ShadowSyncStorage.LoadIndex(_mirrorPath)!;
        Assert.True(index.ContainsKey("a.md"));
        Assert.False(index.ContainsKey("b.md"));
        Assert.False(index.ContainsKey("c.md"));
    }

    [Fact]
    public async Task PushAsync_OnConflict_LeavesLocalFileBaseAndIndexExactlyUntouched_ForTheDivergedCaseThePullLeftBehind()
    {
        Directory.CreateDirectory(_mirrorPath);

        // Exactly what DepotMirrorPullEngine leaves behind for a Diverged file: local edit, stale index entry,
        // untouched base copy — never resolved there, so it must not be silently resolved here either.
        File.WriteAllText(Path.Combine(_mirrorPath, "diverged.md"), "local edit");
        ShadowSyncStorage.WriteBaseFile(_mirrorPath, "diverged.md", "old base body");
        var staleEntry = new ShadowIndexEntry("diverged.md", "sha-old", 999, DateTimeOffset.UtcNow.AddDays(-1));
        ShadowSyncStorage.SaveIndex(_mirrorPath, new Dictionary<string, ShadowIndexEntry> { ["diverged.md"] = staleEntry });

        var client = Substitute.For<IDepotSyncClient>();
        client.WriteManyAsync(ServerName, Project, Arg.Any<IReadOnlyList<DepotWriteEntry>>(), Arg.Any<CancellationToken>())
            .Returns(new DepotWriteManyResult([
                new DepotWriteEntryResult("diverged.md", DepotWriteStatus.Conflict, null, "changed since it was read; current checksum is sha-remote"),
            ]));
        var engine = new DepotMirrorPushEngine(client);

        var result = await engine.PushAsync(_Mirror(), ServerName, Project);

        Assert.Equal(["diverged.md"], result.Conflicted);
        Assert.Equal("local edit", File.ReadAllText(Path.Combine(_mirrorPath, "diverged.md")));
        Assert.Equal("old base body", ShadowSyncStorage.ReadBaseFileIfPresent(_mirrorPath, "diverged.md"));
        Assert.Equal(staleEntry, ShadowSyncStorage.LoadIndex(_mirrorPath)!["diverged.md"]);
    }

    [Fact]
    public async Task PushAsync_ARetainedFileWhoseRemoteWasDeleted_IsNeverRevived_OnlyReportedAsConflict()
    {
        Directory.CreateDirectory(_mirrorPath);

        // Exactly what DepotMirrorPullEngine leaves behind for a Retained file: Depot no longer lists this path
        // at all, but the working copy had itself diverged, so pull kept it — and its stale index entry — rather
        // than deleting it. Pushing this with its old baseChecksum must never resurrect it at Depot.
        File.WriteAllText(Path.Combine(_mirrorPath, "retained.md"), "kept local edit");
        ShadowSyncStorage.WriteBaseFile(_mirrorPath, "retained.md", "old base body");
        var staleEntry = new ShadowIndexEntry("retained.md", "sha-old", 999, DateTimeOffset.UtcNow.AddDays(-1));
        ShadowSyncStorage.SaveIndex(_mirrorPath, new Dictionary<string, ShadowIndexEntry> { ["retained.md"] = staleEntry });

        var client = Substitute.For<IDepotSyncClient>();
        client.WriteManyAsync(ServerName, Project, Arg.Any<IReadOnlyList<DepotWriteEntry>>(), Arg.Any<CancellationToken>())
            .Returns(new DepotWriteManyResult([
                new DepotWriteEntryResult("retained.md", DepotWriteStatus.Conflict, null, "changed since it was read; current checksum is null"),
            ]));
        var engine = new DepotMirrorPushEngine(client);

        var result = await engine.PushAsync(_Mirror(), ServerName, Project);

        // The stale baseChecksum still goes out exactly as recorded — this engine never special-cases a path it
        // suspects Depot deleted; write_many's own conflict on that checksum is what stops the revival.
        await client.Received(1).WriteManyAsync(
            ServerName, Project,
            Arg.Is<IReadOnlyList<DepotWriteEntry>>(e => e.Single().Path == "retained.md" && e.Single().BaseChecksum == "sha-old"),
            Arg.Any<CancellationToken>());
        Assert.Equal(["retained.md"], result.Conflicted);
        Assert.Equal("kept local edit", File.ReadAllText(Path.Combine(_mirrorPath, "retained.md")));
        Assert.Equal(staleEntry, ShadowSyncStorage.LoadIndex(_mirrorPath)!["retained.md"]);
    }

    [Fact]
    public async Task PushAsync_IgnoresAnyChecksumOnAConflictResponse_NeverTreatsItAsConfirmedRemoteState()
    {
        Directory.CreateDirectory(_mirrorPath);
        File.WriteAllText(Path.Combine(_mirrorPath, "x.md"), "local edit");
        ShadowSyncStorage.WriteBaseFile(_mirrorPath, "x.md", "old base");
        var staleEntry = new ShadowIndexEntry("x.md", "sha-old", 999, DateTimeOffset.UtcNow.AddDays(-1));
        ShadowSyncStorage.SaveIndex(_mirrorPath, new Dictionary<string, ShadowIndexEntry> { ["x.md"] = staleEntry });

        var client = Substitute.For<IDepotSyncClient>();
        // Even a conflict response that happens to carry a checksum must never be read as the current remote
        // state (criterion 5) — it is not established that a conflict answer reflects one at all.
        client.WriteManyAsync(ServerName, Project, Arg.Any<IReadOnlyList<DepotWriteEntry>>(), Arg.Any<CancellationToken>())
            .Returns(new DepotWriteManyResult([new DepotWriteEntryResult("x.md", DepotWriteStatus.Conflict, "sha-maybe-remote", "conflict")]));
        var engine = new DepotMirrorPushEngine(client);

        await engine.PushAsync(_Mirror(), ServerName, Project);

        Assert.Equal(staleEntry, ShadowSyncStorage.LoadIndex(_mirrorPath)!["x.md"]);
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
