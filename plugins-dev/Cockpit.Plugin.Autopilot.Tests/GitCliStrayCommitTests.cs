namespace Cockpit.Plugin.Autopilot.Tests;

// Bringing a step's stray commits back onto the run branch (AC-1037), against a real git repository with a real second
// worktree — the shape the bug had. A fake cannot answer the question that matters here: what git does when the
// cherry-pick does not apply, and whether the run's worktree is left as it was found.
public sealed class GitCliStrayCommitTests : IDisposable
{
    private const string RunBranch = "autopilot/run";

    private readonly string _repository = Path.Combine(Path.GetTempPath(), $"ac1037-{Guid.NewGuid():N}");
    private readonly string _stepWorktree = Path.Combine(Path.GetTempPath(), $"ac1037-step-{Guid.NewGuid():N}");
    private readonly GitCliPrPublisher _publisher = new();

    public GitCliStrayCommitTests()
    {
        Directory.CreateDirectory(_repository);
        _Git(_repository, "init");
        _Git(_repository, "config", "user.name", "Test");
        _Git(_repository, "config", "user.email", "test@example.com");
        _Git(_repository, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(_repository, "tracked.txt"), "the run's starting point\n");
        _Git(_repository, "add", "-A");
        _Git(_repository, "commit", "-m", "the run's starting point");
        _Git(_repository, "checkout", "-b", RunBranch);

        // What the host does for a step it hands no shared worktree (AC-434): a fresh worktree on a branch of its own,
        // forked from the run branch's tip.
        _Git(_repository, "worktree", "add", "-b", "cockpit/default-8feeb662", _stepWorktree, RunBranch);
    }

    [Fact]
    public async Task RecoverStrayCommitsAsync_BringsWorkTheStepCommittedOnItsOwnBranch_OntoTheRunBranch()
    {
        File.WriteAllText(Path.Combine(_stepWorktree, "fix.txt"), "the fix the step reported\n");
        _Git(_stepWorktree, "add", "-A");
        _Git(_stepWorktree, "commit", "-m", "the step's fix");

        var stray = await _publisher.RecoverStrayCommitsAsync(_repository, RunBranch, _stepWorktree);

        Assert.Single(stray.Recovered);
        Assert.Empty(stray.Stranded);
        Assert.Null(stray.Error);
        Assert.Equal("the fix the step reported\n", File.ReadAllText(Path.Combine(_repository, "fix.txt")).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task RecoverStrayCommitsAsync_AlsoRecoversWorkTheStepNeverCommitted()
    {
        // The step's worktree is thrown away with its session, so an uncommitted edit there is lost just as silently as
        // a commit on the wrong branch — "no stray commits" would be an all-clear over a change nobody has.
        File.WriteAllText(Path.Combine(_stepWorktree, "fix.txt"), "never committed by the step\n");

        var stray = await _publisher.RecoverStrayCommitsAsync(_repository, RunBranch, _stepWorktree);

        Assert.Single(stray.Recovered);
        Assert.True(File.Exists(Path.Combine(_repository, "fix.txt")));
    }

    [Fact]
    public async Task RecoverStrayCommitsAsync_OnAConflict_StopsAndReportsIt_LeavingTheRunWorktreeAsItWas()
    {
        File.WriteAllText(Path.Combine(_stepWorktree, "tracked.txt"), "what the step decided\n");
        _Git(_stepWorktree, "add", "-A");
        _Git(_stepWorktree, "commit", "-m", "the step's conflicting change");

        File.WriteAllText(Path.Combine(_repository, "tracked.txt"), "what the run already had\n");
        _Git(_repository, "add", "-A");
        _Git(_repository, "commit", "-m", "the run's own change");

        var stray = await _publisher.RecoverStrayCommitsAsync(_repository, RunBranch, _stepWorktree);

        Assert.Empty(stray.Recovered);
        Assert.Single(stray.Stranded);
        Assert.NotNull(stray.Error);

        // Neither half-applied nor quietly resolved: the run's worktree is clean and still says what it said.
        Assert.Equal("what the run already had\n", File.ReadAllText(Path.Combine(_repository, "tracked.txt")).Replace("\r\n", "\n"));
        Assert.Equal(string.Empty, _Git(_repository, "status", "--porcelain").Trim());
    }

    [Fact]
    public async Task RecoverStrayCommitsAsync_ForAStepThatRanInTheRunsOwnWorktree_DoesNothing()
    {
        var stray = await _publisher.RecoverStrayCommitsAsync(_repository, RunBranch, _repository);

        Assert.False(stray.NeedsSaying);
    }

    [Fact]
    public async Task RecoverStrayCommitsAsync_WhenGitWillNotAnswer_ReportsAnUnmeasuredCheck_NotAnAllClear()
    {
        // The path nobody sees: git refuses (here, a run branch that is not a revision) and the answer used to be an
        // empty result — which reads as "nothing went astray" about a step that was never actually checked.
        var stray = await _publisher.RecoverStrayCommitsAsync(_repository, "autopilot/no-such-branch", _stepWorktree);

        Assert.False(stray.Found);
        Assert.True(stray.NeedsSaying);
        Assert.NotNull(stray.Error);
    }

    [Fact]
    public async Task RecoverStrayCommitsAsync_WhenTheStepsWorktreeIsGone_ReportsAnUnmeasuredCheck()
    {
        var stray = await _publisher.RecoverStrayCommitsAsync(_repository, RunBranch, Path.Combine(Path.GetTempPath(), $"ac1037-gone-{Guid.NewGuid():N}"));

        Assert.True(stray.NeedsSaying);
        Assert.NotNull(stray.Error);
    }

    public void Dispose()
    {
        foreach (var directory in new[] { _stepWorktree, _repository })
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception)
            {
                // Throwaway directories under the system temp folder; the OS clears what a locked git file leaves.
            }
        }
    }

    private static string _Git(string worktree, params string[] arguments)
    {
        var result = GitCommandLine.RunAsync("git", arguments, worktree).GetAwaiter().GetResult();
        Assert.True(result.Ok, $"git {string.Join(' ', arguments)} failed: {result.Error}");
        return result.StdOut;
    }
}
