using Cockpit.Infrastructure.Configuration;
using FluentAssertions;

namespace Cockpit.Infrastructure.Tests.Configuration;

/// <summary>
/// The pure pieces of the AC-19 PATH repair: entry detection, the login-shell merge, the fallback prepend and the
/// marker parse. The process-level halves (spawning the login shell, writing the environment) stay untested here —
/// they are thin wiring over these rules, and a test that rewrites the test process's own PATH would sabotage
/// every other test in the run.
/// </summary>
public sealed class StartupPathRepairTests
{
    private static readonly char Separator = Path.PathSeparator;

    private static string Join(params string[] entries) => string.Join(Separator, entries);

    [Fact]
    public void ContainsEntry_WhenTheDirectoryIsOnThePath_IsTrue()
    {
        var path = Join("/usr/local/bin", "/home/user/.local/bin", "/usr/bin");

        StartupPathRepair.ContainsEntry(path, "/home/user/.local/bin").Should().BeTrue();
    }

    [Fact]
    public void ContainsEntry_WhenTheDirectoryIsMissing_IsFalse()
    {
        var path = Join("/usr/local/bin", "/usr/bin");

        StartupPathRepair.ContainsEntry(path, "/home/user/.local/bin").Should().BeFalse();
    }

    [Fact]
    public void ContainsEntry_ToleratesATrailingSlashOnEitherSide()
    {
        StartupPathRepair.ContainsEntry("/home/user/.local/bin/", "/home/user/.local/bin").Should().BeTrue();
        StartupPathRepair.ContainsEntry("/home/user/.local/bin", "/home/user/.local/bin/").Should().BeTrue();
    }

    [Fact]
    public void ContainsEntry_DoesNotMatchAPrefixEntry()
    {
        // "/usr/local" on PATH must not count as "/usr/local/bin" being on it — entries match whole, not by prefix.
        StartupPathRepair.ContainsEntry("/usr/local", "/usr/local/bin").Should().BeFalse();
    }

    [Fact]
    public void ContainsEntry_OnAnEmptyPath_IsFalse()
    {
        StartupPathRepair.ContainsEntry(string.Empty, "/home/user/.local/bin").Should().BeFalse();
    }

    [Fact]
    public void MergePaths_PutsTheLoginShellEntriesFirst_AndKeepsTheCurrentOnlyOnes()
    {
        // The truncated PATH carries entries the login shell does not know (the AppImage mount, dotnet tools);
        // those must survive the merge, after the shell's own ordering.
        var loginShell = Join("/home/user/.local/bin", "/usr/local/bin", "/usr/bin");
        var current = Join("/tmp/.mount_cockpit", "/usr/local/bin", "/usr/bin", "/home/user/.dotnet/tools");

        var merged = StartupPathRepair.MergePaths(loginShell, current);

        merged.Should().Be(Join(
            "/home/user/.local/bin", "/usr/local/bin", "/usr/bin", "/tmp/.mount_cockpit", "/home/user/.dotnet/tools"));
    }

    [Fact]
    public void MergePaths_DeduplicatesATrailingSlashVariant()
    {
        var loginShell = "/usr/bin/";
        var current = "/usr/bin";

        StartupPathRepair.MergePaths(loginShell, current).Should().Be("/usr/bin/");
    }

    [Fact]
    public void PrependMissingEntries_PrependsOnlyTheDirectoriesNotAlreadyOnThePath()
    {
        var path = Join("/home/user/.local/bin", "/usr/bin");

        var repaired = StartupPathRepair.PrependMissingEntries(path, ["/home/user/.local/bin", "/home/user/.bun/bin"]);

        repaired.Should().Be(Join("/home/user/.bun/bin", "/home/user/.local/bin", "/usr/bin"));
    }

    [Fact]
    public void PrependMissingEntries_WhenEverythingIsAlreadyThere_LeavesThePathUntouched()
    {
        var path = Join("/home/user/.local/bin", "/usr/bin");

        StartupPathRepair.PrependMissingEntries(path, ["/home/user/.local/bin"]).Should().Be(path);
    }

    [Fact]
    public void PrependMissingEntries_OnAnEmptyPath_YieldsJustTheDirectories()
    {
        StartupPathRepair.PrependMissingEntries(string.Empty, ["/home/user/bin"]).Should().Be("/home/user/bin");
    }

    [Fact]
    public void ExtractMarkedPath_PullsThePathOutOfNoisyShellOutput()
    {
        var output = $"Welcome to Fedora!\nsome motd line\n{StartupPathRepair.Marker}/usr/local/bin:/usr/bin\n";

        StartupPathRepair.ExtractMarkedPath(output).Should().Be("/usr/local/bin:/usr/bin");
    }

    [Fact]
    public void ExtractMarkedPath_WhenAnInitEchoesTheProbe_TakesTheLastMarkerLine()
    {
        // An init with `set -x` (or an echoing plugin) prints the unexpanded probe before the real answer.
        var output = $"+ echo {StartupPathRepair.Marker}$PATH\n{StartupPathRepair.Marker}/usr/bin\n";

        StartupPathRepair.ExtractMarkedPath(output).Should().Be("/usr/bin");
    }

    [Fact]
    public void ExtractMarkedPath_WithoutAMarkerLine_IsNull()
    {
        StartupPathRepair.ExtractMarkedPath("login: something went wrong\n").Should().BeNull();
    }

    [Fact]
    public void ExtractMarkedPath_WithAnEmptyValue_IsNull()
    {
        StartupPathRepair.ExtractMarkedPath($"{StartupPathRepair.Marker}\n").Should().BeNull();
    }

    [Fact]
    public void UserBinDirectories_CoverTheWellKnownInstallLocations()
    {
        var home = OperatingSystem.IsWindows() ? @"C:\Users\user" : "/home/user";

        StartupPathRepair.UserBinDirectories(home).Should().Equal(
            Path.Combine(home, ".local", "bin"),
            Path.Combine(home, ".bun", "bin"),
            Path.Combine(home, "bin"));
    }
}
