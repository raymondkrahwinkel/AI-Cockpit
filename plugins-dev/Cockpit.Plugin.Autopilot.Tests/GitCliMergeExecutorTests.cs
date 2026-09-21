namespace Cockpit.Plugin.Autopilot.Tests;

// AC-1338: the merge gate against real git (no gh — the fast-forward route is the one a test can drive). What a fake
// cannot answer: that the evidence really shows a deletion of another sub's file, and that a rebase-then-land moves
// the collection branch exactly to the rebased work — or, on a conflict, leaves it and the run branch untouched.
public sealed class GitCliMergeExecutorTests : IDisposable
{
    private const string Collection = "epic/ac-epic";
    private const string RunBranch = "autopilot/run";

    private readonly string _origin = Path.Combine(Path.GetTempPath(), $"ac1338-origin-{Guid.NewGuid():N}");
    private readonly string _clone = Path.Combine(Path.GetTempPath(), $"ac1338-clone-{Guid.NewGuid():N}");
    private readonly GitCliMergeExecutor _executor = new();

    public GitCliMergeExecutorTests()
    {
        Directory.CreateDirectory(_origin);
        _Run(_origin, "init", "--bare");

        Directory.CreateDirectory(_clone);
        _Run(_clone, "init", "-b", "main");
        _Run(_clone, "config", "user.name", "Test");
        _Run(_clone, "config", "user.email", "test@example.com");
        _Run(_clone, "config", "commit.gpgsign", "false");
        _Run(_clone, "remote", "add", "origin", _origin);
        File.WriteAllText(Path.Combine(_clone, "readme.md"), "seed");
        _Run(_clone, "add", "-A");
        _Run(_clone, "commit", "-m", "seed commit");
        _Run(_clone, "push", "-u", "origin", "main");

        // The collection branch, with another sub's work already on it: a file this run is about to delete.
        _Run(_clone, "checkout", "-b", Collection);
        Directory.CreateDirectory(Path.Combine(_clone, "src"));
        File.WriteAllText(Path.Combine(_clone, "src", "OtherSub.cs"), string.Join("\n", Enumerable.Repeat("// another sub's line", 12)));
        _Run(_clone, "add", "-A");
        _Run(_clone, "commit", "-m", "AC-2 - another sub's work");
        _Run(_clone, "push", "-u", "origin", Collection);

        // This run's branch, forked from that tip, modifying the readme and deleting the other sub's file.
        _Run(_clone, "checkout", "-b", RunBranch);
        File.WriteAllText(Path.Combine(_clone, "readme.md"), "the run's version");
        File.Delete(Path.Combine(_clone, "src", "OtherSub.cs"));
        _Run(_clone, "add", "-A");
        _Run(_clone, "commit", "-m", "AC-1 - the run's work");
        _Run(_clone, "push", "-u", "origin", RunBranch);
    }

    [Theory]
    [InlineData("git --version", true)]
    [InlineData("git this-is-not-a-git-command", false)]
    public async Task DescribeAsync_ShowsTheDeletionOfAnotherSubsFile_AndTheBranchsOwnBuild(string buildCommand, bool expectedBuildPassed)
    {
        var evidence = await _executor.DescribeAsync(_clone, Collection, buildCommand);

        Assert.Null(evidence.Error);
        Assert.Equal(_Run(_clone, "rev-parse", "HEAD").Trim(), evidence.HeadSha);
        // Three dots: only this branch's side of the merge base, so the deletion reads as this run's, not as noise.
        Assert.Contains("src/OtherSub.cs | 12 -", evidence.DiffStat);
        Assert.Equal(expectedBuildPassed, evidence.BuildPassed);
    }

    [Theory]
    [InlineData("other.txt", true, false)]
    [InlineData("readme.md", false, true)]
    public async Task MergeAsync_RebasesOntoTheMovedCollectionBranch_AndLandsOnlyWhenItAppliesCleanly(string fileAnotherSubTouchesMeanwhile, bool expectedMerged, bool expectedRunBranchStillOnRemote)
    {
        // Another sub lands on the collection branch after this run forked (EpicWorkflow §6c's "commits land meanwhile").
        _Run(_clone, "checkout", Collection);
        File.WriteAllText(Path.Combine(_clone, fileAnotherSubTouchesMeanwhile), "another sub's version");
        _Run(_clone, "add", "-A");
        _Run(_clone, "commit", "-m", "AC-3 - landed meanwhile");
        _Run(_clone, "push", "origin", Collection);
        var collectionBefore = _Run(_origin, "rev-parse", Collection).Trim();
        _Run(_clone, "checkout", RunBranch);

        var result = await _executor.MergeAsync(new AutopilotMergeRequest(_clone, RunBranch, Collection, PrUrl: null, "git --version"));

        Assert.Equal(expectedMerged, result.Merged);
        var collectionAfter = _Run(_origin, "rev-parse", Collection).Trim();
        var runBranchOnRemote = _Run(_origin, "for-each-ref", $"refs/heads/{RunBranch}", "--format=%(objectname)").Trim();
        Assert.Equal(expectedRunBranchStillOnRemote, runBranchOnRemote.Length > 0);
        // Merged: the remote collection tip is the rebased run work (it contains the meanwhile commit and the run's
        // readme), the worktree stands on it, and the build ran there. Refused: the remote collection did not move.
        Assert.Equal(expectedMerged, collectionAfter != collectionBefore);
        Assert.Equal(expectedMerged, result.TipSha == collectionAfter);
        Assert.Equal(expectedMerged, result.BuildExitCode == 0);
        Assert.Equal(expectedMerged, _Run(_clone, "log", "--format=%s", "-3").Contains("AC-3 - landed meanwhile", StringComparison.Ordinal));
        // Either way the worktree is left clean and on the run branch — an aborted rebase leaves no half-applied state behind.
        Assert.Equal(string.Empty, _Run(_clone, "status", "--porcelain").Trim());
        Assert.Equal(RunBranch, _Run(_clone, "rev-parse", "--abbrev-ref", "HEAD").Trim());
    }

    public void Dispose()
    {
        _TryDelete(_origin);
        _TryDelete(_clone);
    }

    private static void _TryDelete(string path)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
            // A throwaway directory under the system temp folder.
        }
    }

    private static string _Run(string directory, params string[] arguments)
    {
        var result = GitCommandLine.RunAsync("git", arguments, directory).GetAwaiter().GetResult();
        Assert.True(result.Ok, $"git {string.Join(' ', arguments)} failed: {result.Error}");
        return result.StdOut;
    }
}
