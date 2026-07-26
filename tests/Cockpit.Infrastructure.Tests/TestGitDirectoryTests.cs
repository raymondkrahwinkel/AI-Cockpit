using System.Diagnostics;
using FluentAssertions;

namespace Cockpit.Infrastructure.Tests;

/// <summary>
/// The teardown every git-backed fixture in this project leans on (AC-339). Worth a test of its own precisely
/// because it is a test helper: when it throws, it throws from <c>Dispose</c>, so the class it belongs to reports
/// every one of its tests as failed and none of the messages mention the real reason.
/// </summary>
public sealed class TestGitDirectoryTests
{
    /// <summary>
    /// The regression: a repository with a commit in it has read-only loose objects, and on Windows those make a
    /// plain recursive delete throw. This is red without <see cref="TestGitDirectory"/>'s attribute pass.
    /// </summary>
    [Fact]
    public void ARepositoryWithACommitInIt_IsRemovedRatherThanRefused()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cockpit-testgitdir-{Guid.NewGuid():n}");
        Directory.CreateDirectory(root);
        _Git(root, "init", "-b", "main");
        _Git(root, "config", "user.email", "test@example.com");
        _Git(root, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(root, "README.md"), "hello\n");
        _Git(root, "add", "-A");
        _Git(root, "commit", "-m", "first");

        TestGitDirectory.Remove(root);

        Directory.Exists(root).Should().BeFalse("the tree is gone, not merely attempted");
    }

    /// <summary>A fixture whose temp root was never created still calls this, so it has to be a no-op rather than a throw.</summary>
    [Fact]
    public void ADirectoryThatWasNeverThere_IsLeftAlone()
    {
        var never = Path.Combine(Path.GetTempPath(), $"cockpit-testgitdir-{Guid.NewGuid():n}");

        var act = () => TestGitDirectory.Remove(never);

        act.Should().NotThrow();
    }

    private static void _Git(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git.");
        process.WaitForExit();
    }
}
