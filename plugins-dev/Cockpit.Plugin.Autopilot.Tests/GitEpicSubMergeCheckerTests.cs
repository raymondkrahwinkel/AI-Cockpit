namespace Cockpit.Plugin.Autopilot.Tests;

// AC-346: whether an epic's sub is already merged, checked against a real `origin/main` — not a local branch or
// worktree, since a run's own worktree can carry a sub's commits before a human has actually merged its PR. Runs git
// for real on a throwaway bare "origin" plus a clone, the same style as `GitCliEvidenceSourceTests`.
public sealed class GitEpicSubMergeCheckerTests : IDisposable
{
    private readonly string _origin = Path.Combine(Path.GetTempPath(), $"ac346-origin-{Guid.NewGuid():N}");
    private readonly string _clone = Path.Combine(Path.GetTempPath(), $"ac346-clone-{Guid.NewGuid():N}");

    public GitEpicSubMergeCheckerTests()
    {
        Directory.CreateDirectory(_origin);
        _Run(_origin, "init", "--bare").GetAwaiter().GetResult();

        Directory.CreateDirectory(_clone);
        _Run(_clone, "init").GetAwaiter().GetResult();
        _Run(_clone, "checkout", "-b", "main").GetAwaiter().GetResult();
        _Run(_clone, "remote", "add", "origin", _origin).GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(_clone, "readme.md"), "seed");
        _Run(_clone, "add", "-A").GetAwaiter().GetResult();
        _Run(_clone, "-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "commit", "-m", "AC-1 - seed commit").GetAwaiter().GetResult();
        _Run(_clone, "push", "-u", "origin", "main").GetAwaiter().GetResult();
    }

    private static async Task<GitEpicSubMergeChecker> _Refreshed(string directory)
    {
        var checker = new GitEpicSubMergeChecker(directory);
        await checker.RefreshAsync();
        return checker;
    }

    [Fact]
    public async Task IsMerged_ForAnIssueWithACommitOnOriginMain_ReturnsTrue()
    {
        var checker = await _Refreshed(_clone);

        Assert.True(checker.IsMerged("AC-1"));
    }

    // AC-346 review, BLOCKING finding #2: `git log --grep="^AC-3"` also matches "AC-34 - …" / "AC-350 - …" — a prefix
    // collision that would read a sibling sub as already merged. Reproduced here with both colliding ids actually
    // present in history, on the same clone AC-1 (the seed commit) already lives in.
    [Fact]
    public async Task IsMerged_DoesNotMatchALongerIdSharingTheSamePrefix()
    {
        await _Commit("AC-34 - unrelated commit that happens to start with the same digits as AC-3");
        await _Commit("AC-350 - another unrelated commit with a colliding numeric prefix");
        await _Run(_clone, "push", "origin", "main");

        var checker = await _Refreshed(_clone);

        Assert.False(checker.IsMerged("AC-3"));
        // The longer ids themselves are still found correctly — this is a precision fix, not a recall regression.
        Assert.True(checker.IsMerged("AC-34"));
        Assert.True(checker.IsMerged("AC-350"));
    }

    // AC-346 review, BLOCKING finding #2 (second half): only the commit subject (first line) counts — a ticket id
    // mentioned in passing somewhere in the body must not count as "this sub is merged".
    [Fact]
    public async Task IsMerged_IgnoresAnIdThatOnlyAppearsInTheCommitBody_NotTheSubjectLine()
    {
        await _Run(
            _clone,
            "-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false",
            "commit", "--allow-empty",
            "-m", "AC-77 - subject line",
            "-m", "- fixed: also touches AC-88 in passing, mentioned here in the body only");
        await _Run(_clone, "push", "origin", "main");

        var checker = await _Refreshed(_clone);

        Assert.True(checker.IsMerged("AC-77"));
        Assert.False(checker.IsMerged("AC-88"));
    }

    // AC-1337: a collection branch, when given, is checked instead of main — a sub merged only into the collection
    // branch reads as merged there and as not-yet-merged when no collection branch is set (main never saw it).

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

    private async Task _Commit(string message) =>
        await _Run(_clone, "-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "commit", "--allow-empty", "-m", message);

    private static async Task<string> _Run(string directory, params string[] arguments)
    {
        var result = await GitCommandLine.RunAsync("git", arguments, directory);
        Assert.True(result.Ok, $"git {string.Join(' ', arguments)} failed: {result.Error}");
        return result.StdOut;
    }
}
