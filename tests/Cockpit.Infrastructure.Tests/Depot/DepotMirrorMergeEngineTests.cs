using Cockpit.Core.Abstractions.Depot;
using Cockpit.Core.Depot;
using Cockpit.Infrastructure.Depot;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Depot;

/// <summary>
/// One counter-example per AC-283 acceptance criterion 1-5, plus one extra above that budget for the trap the
/// ticket calls out by name: a conflicting merge must never touch the shadow base/index, or the next push would
/// silently overwrite instead of conflicting. These invoke the real <c>git merge-file</c> binary rather than a
/// stub — unlike Depot's own contract, git's diff3 behavior is a local, already-measured tool, not something to
/// fake the answer of.
/// </summary>
public sealed class DepotMirrorMergeEngineTests : IDisposable
{
    private readonly string _mirrorPath = Path.Combine(Path.GetTempPath(), $"cockpit-depotmerge-{Guid.NewGuid():n}");
    private const string ServerName = "Depot: Work";
    private const string Project = "acme";

    // git merge-file's diff3 algorithm needs enough unchanged context around two edits before it treats them as
    // independent, non-conflicting hunks — measured directly against the real binary; three adjacent lines is not
    // enough and merges "cleanly" into a conflict. Ten lines with the two edits far apart is.
    private const string _WideBase = "a1\na2\na3\na4\na5\na6\na7\na8\na9\na10\n";
    private const string _WideLocalEdit = "a1\na2-local\na3\na4\na5\na6\na7\na8\na9\na10\n";
    private const string _WideRemoteEdit = "a1\na2\na3\na4\na5\na6\na7\na8\na9-remote\na10\n";

    [Fact]
    public async Task MergeAsync_ACleanMerge_WritesTheResolvedContent_AndRebasesShadowOntoDepotsChecksum()
    {
        var oldEntry = _SeedDivergedFile("notes.md", baseBody: _WideBase, localBody: _WideLocalEdit);

        var client = Substitute.For<IDepotSyncClient>();
        client.ReadManyAsync(ServerName, Project, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(DepotReadManyResult.Success(
                [new DepotReadFile("notes.md", _WideRemoteEdit, "sha-remote-actual")], [], []));
        var engine = new DepotMirrorMergeEngine(client);

        var result = await engine.MergeAsync(_Mirror(), ServerName, Project, [new DepotDivergedFile("notes.md", BaseConfirmed: true)]);

        Assert.Equal(["notes.md"], result.Merged);
        Assert.Empty(result.Conflicted);

        var working = File.ReadAllText(Path.Combine(_mirrorPath, "notes.md"));
        Assert.Contains("a2-local", working);
        Assert.Contains("a9-remote", working);
        Assert.DoesNotContain("<<<<<<<", working);

        Assert.Equal(_WideRemoteEdit, ShadowSyncStorage.ReadBaseFileIfPresent(_mirrorPath, "notes.md"));

        var newEntry = ShadowSyncStorage.LoadIndex(_mirrorPath)!["notes.md"];
        Assert.Equal("sha-remote-actual", newEntry.BaseChecksum);
        // Deliberately still the pre-merge stat, not the freshly written file's — matching it would make the
        // push engine's own divergence check think this file is already in sync and skip pushing it.
        Assert.Equal(oldEntry.Size, newEntry.Size);
        Assert.Equal(oldEntry.Mtime, newEntry.Mtime);
    }

    [Fact]
    public async Task MergeAsync_AConflictingMerge_WritesDiff3MarkersIntoTheWorkingFile()
    {
        _SeedDivergedFile("notes.md", baseBody: "line1\nline2\nline3\n", localBody: "line1\nline2-local\nline3\n");

        var client = Substitute.For<IDepotSyncClient>();
        client.ReadManyAsync(ServerName, Project, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(DepotReadManyResult.Success(
                [new DepotReadFile("notes.md", "line1\nline2-remote\nline3\n", "sha-remote-actual")], [], []));
        var engine = new DepotMirrorMergeEngine(client);

        var result = await engine.MergeAsync(_Mirror(), ServerName, Project, [new DepotDivergedFile("notes.md", BaseConfirmed: true)]);

        Assert.Empty(result.Merged);
        Assert.Equal("notes.md", result.Conflicted.Single().Path);

        var working = File.ReadAllText(Path.Combine(_mirrorPath, "notes.md"));
        Assert.Contains("<<<<<<<", working);
        Assert.Contains("|||||||", working);
        Assert.Contains(">>>>>>>", working);
        Assert.Contains("line2-local", working);
        Assert.Contains("line2-remote", working);
    }

    [Fact]
    public async Task MergeAsync_AConflictingMerge_LeavesTheShadowBaseAndIndexExactlyUntouched()
    {
        var oldEntry = _SeedDivergedFile("notes.md", baseBody: "line1\nline2\nline3\n", localBody: "line1\nline2-local\nline3\n");

        var client = Substitute.For<IDepotSyncClient>();
        client.ReadManyAsync(ServerName, Project, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(DepotReadManyResult.Success(
                [new DepotReadFile("notes.md", "line1\nline2-remote\nline3\n", "sha-remote-actual")], [], []));
        var engine = new DepotMirrorMergeEngine(client);

        await engine.MergeAsync(_Mirror(), ServerName, Project, [new DepotDivergedFile("notes.md", BaseConfirmed: true)]);

        // The sharpest trap in this ticket: updating either of these on a marked conflict would make the next
        // push an overwrite instead of the conflict it still is.
        Assert.Equal("line1\nline2\nline3\n", ShadowSyncStorage.ReadBaseFileIfPresent(_mirrorPath, "notes.md"));
        Assert.Equal(oldEntry, ShadowSyncStorage.LoadIndex(_mirrorPath)!["notes.md"]);
    }

    [Fact]
    public async Task MergeAsync_AnUnconfirmedBase_IsNeverMerged_AndNothingOnDiskIsTouched()
    {
        var oldEntry = _SeedDivergedFile("notes.md", baseBody: "line1\nline2\nline3\n", localBody: "line1\nline2-local\nline3\n");
        var baseBefore = ShadowSyncStorage.ReadBaseFileIfPresent(_mirrorPath, "notes.md");
        var workingBefore = File.ReadAllText(Path.Combine(_mirrorPath, "notes.md"));

        var client = Substitute.For<IDepotSyncClient>();
        var engine = new DepotMirrorMergeEngine(client);

        var result = await engine.MergeAsync(_Mirror(), ServerName, Project, [new DepotDivergedFile("notes.md", BaseConfirmed: false)]);

        Assert.Empty(result.Merged);
        Assert.Equal("notes.md", result.Conflicted.Single().Path);
        await client.DidNotReceiveWithAnyArgs().ReadManyAsync(default!, default!, default!);
        Assert.Equal(workingBefore, File.ReadAllText(Path.Combine(_mirrorPath, "notes.md")));
        Assert.Equal(baseBefore, ShadowSyncStorage.ReadBaseFileIfPresent(_mirrorPath, "notes.md"));
        Assert.Equal(oldEntry, ShadowSyncStorage.LoadIndex(_mirrorPath)!["notes.md"]);
    }

    [Fact]
    public async Task MergeAsync_OneFilesConflictDoesNotBlockAnotherFilesCleanMerge()
    {
        _SeedDivergedFile("conflicting.md", baseBody: "line1\nline2\nline3\n", localBody: "line1\nline2-local\nline3\n");
        _SeedDivergedFile("clean.md", baseBody: _WideBase, localBody: _WideLocalEdit);

        var client = Substitute.For<IDepotSyncClient>();
        client.ReadManyAsync(ServerName, Project, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(DepotReadManyResult.Success(
                [
                    new DepotReadFile("conflicting.md", "line1\nline2-remote\nline3\n", "sha-conflicting-remote"),
                    new DepotReadFile("clean.md", _WideRemoteEdit, "sha-clean-remote"),
                ], [], []));
        var engine = new DepotMirrorMergeEngine(client);

        var result = await engine.MergeAsync(
            _Mirror(), ServerName, Project,
            [new DepotDivergedFile("conflicting.md", BaseConfirmed: true), new DepotDivergedFile("clean.md", BaseConfirmed: true)]);

        Assert.Equal(["clean.md"], result.Merged);
        Assert.Equal("conflicting.md", result.Conflicted.Single().Path);
        Assert.Equal("sha-clean-remote", ShadowSyncStorage.LoadIndex(_mirrorPath)!["clean.md"].BaseChecksum);
    }

    [Fact]
    public async Task MergeAsync_BinaryContent_IsReportedAsUnresolvable_NotAutomaticallyChosen_AndNothingIsTouched()
    {
        var oldEntry = _SeedDivergedFile("blob.md", baseBody: "plain base", localBody: "local with a \0 byte");

        var client = Substitute.For<IDepotSyncClient>();
        client.ReadManyAsync(ServerName, Project, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(DepotReadManyResult.Success([new DepotReadFile("blob.md", "remote body", "sha-remote")], [], []));
        var engine = new DepotMirrorMergeEngine(client);

        var result = await engine.MergeAsync(_Mirror(), ServerName, Project, [new DepotDivergedFile("blob.md", BaseConfirmed: true)]);

        Assert.Empty(result.Merged);
        Assert.Equal("blob.md", result.Conflicted.Single().Path);
        Assert.Equal("local with a \0 byte", File.ReadAllText(Path.Combine(_mirrorPath, "blob.md")));
        Assert.Equal("plain base", ShadowSyncStorage.ReadBaseFileIfPresent(_mirrorPath, "blob.md"));
        Assert.Equal(oldEntry, ShadowSyncStorage.LoadIndex(_mirrorPath)!["blob.md"]);
    }

    // Lays down exactly what AC-281's pull leaves behind for a diverged file: a base copy, a locally-edited
    // working file, and a stale index entry recorded before that local edit.
    private ShadowIndexEntry _SeedDivergedFile(string path, string baseBody, string localBody)
    {
        Directory.CreateDirectory(_mirrorPath);
        ShadowSyncStorage.WriteBaseFile(_mirrorPath, path, baseBody);
        File.WriteAllText(Path.Combine(_mirrorPath, path), localBody);

        var entry = new ShadowIndexEntry(path, "sha-old", 999, DateTimeOffset.UtcNow.AddDays(-1));
        var index = ShadowSyncStorage.LoadIndex(_mirrorPath)!.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        index[path] = entry;
        ShadowSyncStorage.SaveIndex(_mirrorPath, index);
        return entry;
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
