namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// AC-346: whether an epic's sub is already merged, checked against a real <c>origin/main</c> — not a local branch or
/// worktree, since a run's own worktree can carry a sub's commits before a human has actually merged its PR. Runs git
/// for real on a throwaway bare "origin" plus a clone, the same style as <see cref="GitCliEvidenceSourceTests"/>.
/// </summary>
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

    [Fact]
    public async Task IsMergedAsync_ForAnIssueWithACommitOnOriginMain_ReturnsTrue()
    {
        var checker = new GitEpicSubMergeChecker(_clone);

        Assert.True(await checker.IsMergedAsync("AC-1"));
    }

    [Fact]
    public async Task IsMergedAsync_ForAnIssueNeverCommitted_ReturnsFalse()
    {
        var checker = new GitEpicSubMergeChecker(_clone);

        Assert.False(await checker.IsMergedAsync("AC-999"));
    }

    [Fact]
    public async Task IsMergedAsync_OnlyLooksAtOriginMain_NotALocalBranchAheadOfIt()
    {
        // A commit only on a local branch (the run's own worktree, never pushed/merged) must not read as "merged" —
        // that is the whole point of checking origin/main and not a local ref.
        await _Run(_clone, "checkout", "-b", "run/ac-2");
        File.WriteAllText(Path.Combine(_clone, "work.md"), "local work");
        await _Run(_clone, "add", "-A");
        await _Run(_clone, "-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "commit", "-m", "AC-2 - local only, never merged");

        var checker = new GitEpicSubMergeChecker(_clone);

        Assert.False(await checker.IsMergedAsync("AC-2"));
    }

    [Fact]
    public async Task IsMergedAsync_ForAFolderThatIsNotAGitWorktree_ReturnsFalse()
    {
        var plain = Path.Combine(Path.GetTempPath(), $"ac346-plain-{Guid.NewGuid():N}");
        Directory.CreateDirectory(plain);

        try
        {
            var checker = new GitEpicSubMergeChecker(plain);
            Assert.False(await checker.IsMergedAsync("AC-1"));
        }
        finally
        {
            Directory.Delete(plain, recursive: true);
        }
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

    private static async Task<string> _Run(string directory, params string[] arguments)
    {
        var result = await GitCommandLine.RunAsync("git", arguments, directory);
        Assert.True(result.Ok, $"git {string.Join(' ', arguments)} failed: {result.Error}");
        return result.StdOut;
    }
}
