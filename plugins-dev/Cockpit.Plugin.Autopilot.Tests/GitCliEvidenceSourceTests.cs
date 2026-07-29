namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The evidence source against a real git repository (AC-255). These run git for real on a throwaway repo, because the
/// question they answer cannot be faked: whether "what this step changed" is measured from the right moment. An
/// adversarial review found that a mark of <c>HEAD</c> alone credited a step with work an earlier step had left
/// uncommitted, and no test could have seen it — every other test in this suite hands the source's output in
/// ready-made.
/// </summary>
public sealed class GitCliEvidenceSourceTests : IDisposable
{
    private readonly string _repository = Path.Combine(Path.GetTempPath(), $"ac255-{Guid.NewGuid():N}");
    private readonly GitCliEvidenceSource _source = new();

    public GitCliEvidenceSourceTests()
    {
        Directory.CreateDirectory(_repository);
        _Git("init").GetAwaiter().GetResult();
        _Write("tracked.txt", "one");
        _Git("add", "-A").GetAwaiter().GetResult();
        _Commit("the run's starting point").GetAwaiter().GetResult();
    }

    [Fact]
    public async Task CollectAsync_DoesNotCreditTheStep_WithWorkAnEarlierStepLeftUncommitted()
    {
        // The defect this test exists for: nothing commits between ordinary steps, so a mark that only remembered HEAD
        // would show the previous step's uncommitted edit as this step's own — and, because the diff was not empty,
        // the "reported work but nothing changed" spot-check would stay silent about a step that did nothing at all.
        _Write("tracked.txt", "an earlier step changed this and never committed it");

        var mark = await _source.MarkAsync(_repository);
        Assert.NotNull(mark);

        // This step does nothing whatsoever.
        var change = await _source.CollectAsync(_repository, mark);

        Assert.NotNull(change);
        Assert.True(change.IsEmpty, $"expected no change, got {string.Join(", ", change.FilesChanged)} / {string.Join(", ", change.UntrackedFiles)}");
    }

    [Fact]
    public async Task CollectAsync_ReportsOnlyUntrackedFilesThatWereNotAlreadyThere()
    {
        _Write("left-behind.txt", "an earlier step wrote this and never added it");

        var mark = await _source.MarkAsync(_repository);
        Assert.NotNull(mark);

        _Write("this-step.txt", "the step's own new file");
        var change = await _source.CollectAsync(_repository, mark);

        Assert.NotNull(change);
        Assert.Equal(["this-step.txt"], change.UntrackedFiles);
    }

    [Fact]
    public async Task CollectAsync_SeesWorkTheStepCommitted_AndWorkItLeftUncommitted()
    {
        var mark = await _source.MarkAsync(_repository);
        Assert.NotNull(mark);

        _Write("tracked.txt", "the step committed this");
        await _Git("add", "-A");
        await _Commit("the step's own commit");
        _Write("also-uncommitted.txt", "and left this lying");
        await _Git("add", "also-uncommitted.txt");

        var change = await _source.CollectAsync(_repository, mark);

        Assert.NotNull(change);
        Assert.Contains("tracked.txt", change.FilesChanged);
        Assert.Contains("also-uncommitted.txt", change.FilesChanged);
        Assert.Contains("the step committed this", change.Patch);
    }

    [Fact]
    public async Task CollectAsync_SinglesOutAFileTheStepOnlyStaged_ThatWasAlreadyLyingThere()
    {
        // A file an earlier step wrote but never added crosses into the tracked half the moment this step runs
        // `git add -A`, and then reads as brand new in the diff. It is a real change to the repository, so it stays —
        // but its contents are not this step's work, and the CEO is told which files those are.
        _Write("left-behind.txt", "an earlier step wrote this");

        var mark = await _source.MarkAsync(_repository);
        Assert.NotNull(mark);

        await _Git("add", "-A");
        var change = await _source.CollectAsync(_repository, mark);

        Assert.NotNull(change);
        Assert.Contains("left-behind.txt", change.FilesChanged);
        Assert.Equal(["left-behind.txt"], change.AddedFromBeforeTheMark);
    }

    [Fact]
    public async Task MarkAsync_PinsTheDirtyWorktree_NotMerelyHead()
    {
        // The distinction the whole mark rests on: with uncommitted work present, the pinned commit must be a snapshot
        // of the worktree, not HEAD — otherwise that work is measured as the next step's.
        _Write("tracked.txt", "uncommitted");

        var mark = await _source.MarkAsync(_repository);
        var head = (await _Git("rev-parse", "HEAD")).Trim();

        Assert.NotNull(mark);
        Assert.NotEqual(head, mark.Commit);
    }

    [Fact]
    public async Task MarkAsync_ForACleanWorktree_PinsHead()
    {
        var mark = await _source.MarkAsync(_repository);
        var head = (await _Git("rev-parse", "HEAD")).Trim();

        Assert.NotNull(mark);
        Assert.Equal(head, mark.Commit);
    }

    [Fact]
    public async Task MarkAsync_LeavesTheWorktreeExactlyAsItFoundIt()
    {
        // An observation that mutates what it observes is not evidence — and `stash create` is only safe here because
        // it writes its snapshot nowhere.
        _Write("tracked.txt", "uncommitted work that must survive being observed");
        _Write("untracked.txt", "and so must this");

        Assert.NotNull(await _source.MarkAsync(_repository));

        Assert.Equal("uncommitted work that must survive being observed", File.ReadAllText(Path.Combine(_repository, "tracked.txt")));
        Assert.Equal("and so must this", File.ReadAllText(Path.Combine(_repository, "untracked.txt")));
        var status = await _Git("status", "--porcelain");
        Assert.Contains("tracked.txt", status);
        Assert.Contains("untracked.txt", status);
    }

    [Fact]
    public async Task MarkAsync_WhenGitRefusesToSnapshotTheWorktree_ReturnsNull_RatherThanQuietlyPinningHead()
    {
        // The guard that keeps a failure from becoming a wrong answer. A conflicted merge is one state git will not
        // snapshot; falling back to HEAD there would hand the next step the whole merge as its own work, and nothing
        // in the validation turn would hint that anything had gone wrong.
        await _Git("checkout", "-b", "other");
        _Write("tracked.txt", "the other branch");
        await _Git("add", "-A");
        await _Commit("a conflicting change");
        await _Git("checkout", "-");
        _Write("tracked.txt", "the first branch");
        await _Git("add", "-A");
        await _Commit("the other side of the conflict");

        var merge = await GitCommandLine.RunAsync("git", ["merge", "other"], _repository);
        Assert.False(merge.Ok, "the merge was supposed to conflict");

        Assert.Null(await _source.MarkAsync(_repository));
    }

    [Fact]
    public async Task MarkAsync_ForAFolderThatIsNotAGitWorktree_ReturnsNull()
    {
        var plain = Path.Combine(Path.GetTempPath(), $"ac255-plain-{Guid.NewGuid():N}");
        Directory.CreateDirectory(plain);

        try
        {
            Assert.Null(await _source.MarkAsync(plain));
        }
        finally
        {
            Directory.Delete(plain, recursive: true);
        }
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_repository, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_repository, recursive: true);
        }
        catch (Exception)
        {
            // A throwaway directory under the system temp folder; the OS cleans up what a locked git file leaves.
        }
    }

    private void _Write(string name, string content) => File.WriteAllText(Path.Combine(_repository, name), content);

    private async Task<string> _Git(params string[] arguments)
    {
        var result = await GitCommandLine.RunAsync("git", arguments, _repository);
        Assert.True(result.Ok, $"git {string.Join(' ', arguments)} failed: {result.Error}");
        return result.StdOut;
    }

    private Task<string> _Commit(string message) =>
        _Git("-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "commit", "-m", message);
}
