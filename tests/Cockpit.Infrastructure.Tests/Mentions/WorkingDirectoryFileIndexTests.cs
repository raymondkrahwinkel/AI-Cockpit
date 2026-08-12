using System.Diagnostics;
using Cockpit.Infrastructure.Mentions;
using Cockpit.TestSupport;

namespace Cockpit.Infrastructure.Tests.Mentions;

/// <summary>
/// AC-740's real file source: <c>git ls-files</c> in a repository (tracked + untracked-not-ignored, gitignore
/// respected for free), an enumerate-fallback with a skiplist outside one, and a TTL cache so the same directory
/// isn't rescanned on every '@'.
/// </summary>
public sealed class WorkingDirectoryFileIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cockpit-mentionindex-{Guid.NewGuid():n}");

    public WorkingDirectoryFileIndexTests() => Directory.CreateDirectory(_root);

    public void Dispose() => TestGitDirectory.Remove(_root);

    private void _InitRepo()
    {
        _Git("init", "-b", "main");
        _Git("config", "user.email", "test@example.com");
        _Git("config", "user.name", "Test");
    }

    private void _Git(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git") { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git.");
        process.WaitForExit();
    }

    private void _Write(string relativePath, string content = "x")
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public async Task GetPathsAsync_InAGitRepo_ReturnsTrackedAndUntrackedButNotIgnoredFiles()
    {
        _InitRepo();
        _Write("README.md");
        _Git("add", "README.md");
        _Git("commit", "-m", "first");
        _Write("untracked.txt");
        _Write(".gitignore", "ignored.txt\n");
        _Write("ignored.txt");

        var paths = await new WorkingDirectoryFileIndex().GetPathsAsync(_root, CancellationToken.None);

        Assert.Contains("README.md", paths);
        Assert.Contains("untracked.txt", paths);
        Assert.DoesNotContain("ignored.txt", paths);
    }

    [Fact]
    public async Task GetPathsAsync_InAGitRepo_IncludesAncestorDirectoriesWithATrailingSlash()
    {
        _InitRepo();
        _Write("src/Views/SessionView.axaml");
        _Git("add", "-A");
        _Git("commit", "-m", "first");

        var paths = await new WorkingDirectoryFileIndex().GetPathsAsync(_root, CancellationToken.None);

        Assert.Contains("src/", paths);
        Assert.Contains("src/Views/", paths);
        Assert.Contains("src/Views/SessionView.axaml", paths);
    }

    [Fact]
    public async Task GetPathsAsync_OutsideAGitRepo_FallsBackToEnumeration()
    {
        _Write("plain.txt");
        _Write("src/nested.txt");

        var paths = await new WorkingDirectoryFileIndex().GetPathsAsync(_root, CancellationToken.None);

        Assert.Contains("plain.txt", paths);
        Assert.Contains("src/nested.txt", paths);
        Assert.Contains("src/", paths);
    }

    [Fact]
    public async Task GetPathsAsync_OutsideAGitRepo_SkipsKnownNoiseDirectories()
    {
        _Write("real.txt");
        _Write("node_modules/some-package/index.js");

        var paths = await new WorkingDirectoryFileIndex().GetPathsAsync(_root, CancellationToken.None);

        Assert.Contains("real.txt", paths);
        Assert.DoesNotContain(paths, p => p.Contains("node_modules", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetPathsAsync_ADirectoryThatDoesNotExist_ReturnsEmptyRatherThanThrowing()
    {
        var missing = Path.Combine(_root, "does-not-exist");

        var paths = await new WorkingDirectoryFileIndex().GetPathsAsync(missing, CancellationToken.None);

        Assert.Empty(paths);
    }

    [Fact]
    public async Task GetPathsAsync_CalledAgainWithinTheTtl_ServesTheCachedSnapshotWithoutRescanning()
    {
        _Write("first.txt");
        var index = new WorkingDirectoryFileIndex();

        var first = await index.GetPathsAsync(_root, CancellationToken.None);
        Assert.Contains("first.txt", first);

        // A file that appeared after the first build would show up on a rescan — its absence is the proof the
        // second call served the cached snapshot instead of touching the disk again.
        _Write("second.txt");
        var second = await index.GetPathsAsync(_root, CancellationToken.None);

        Assert.DoesNotContain("second.txt", second);
        Assert.Equal(first, second);
    }
}
