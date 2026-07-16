using System.Diagnostics;
using Cockpit.Infrastructure.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Tests.Configuration;

/// <summary>
/// The AC-19 PATH repair: the pure rules (entry detection, the login-shell merge, the fallback prepend, the
/// marker parse) plus the one hard promise of the process half — the login-shell probe answers or gives up
/// within its deadline, driven with fake shells. Only <c>Run</c> itself stays untested here: it rewrites the
/// test process's own PATH, which would sabotage every other test in the run.
/// </summary>
public sealed class StartupPathRepairTests
{
    private static readonly char Separator = Path.PathSeparator;

    private static string Join(params string[] entries) => string.Join(Separator, entries);

    // A fake login shell: an executable script that ignores the -l -c probe arguments and runs its own body.
    // The probe tests never run on Windows (they return early, as Run itself is Unix-gated), but the platform
    // analyzer (CA1416) cannot see through that — hence the explicit guard around the Unix-only chmod.
    private static string WriteFakeShell(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cockpit-fake-shell-{Guid.NewGuid():N}.sh");
        File.WriteAllText(path, $"#!/bin/sh\n{body}\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

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
    public void ReadLoginShellPath_FromAnAnsweringShell_ReturnsItsMarkedPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // The probe never runs on Windows (Run is Unix-gated), and a .sh fake shell cannot either.
        }

        var shell = WriteFakeShell("echo \"__COCKPIT_LOGIN_PATH__=/fake/login/bin:/usr/bin\"");
        try
        {
            var path = StartupPathRepair.ReadLoginShellPath(shell, TimeSpan.FromSeconds(5), NullLogger.Instance);

            path.Should().Be("/fake/login/bin:/usr/bin");
        }
        finally
        {
            File.Delete(shell);
        }
    }

    [Fact]
    public void ReadLoginShellPath_WhenTheShellWedges_GivesUpWithinTheDeadline()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // A shell wedged on its init (never prints, never exits) — the probe must give up and take the fallback.
        var shell = WriteFakeShell("sleep 30");
        try
        {
            var elapsed = Stopwatch.StartNew();
            var path = StartupPathRepair.ReadLoginShellPath(shell, TimeSpan.FromMilliseconds(500), NullLogger.Instance);
            elapsed.Stop();

            path.Should().BeNull();
            elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        }
        finally
        {
            File.Delete(shell);
        }
    }

    [Fact]
    public void ReadLoginShellPath_WhenABackgroundChildHoldsTheStdoutPipe_IsBoundedByOneDeadlineNotTwo()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // The shell burns most of the deadline on its init, then exits leaving a background child that inherited
        // stdout — EOF never arrives, so the stdout read must be bounded by the REMAINDER of the same deadline.
        // Two full waits in a row (exit + read) would land at ~1.8s here; one shared deadline stays at ~1s.
        var shell = WriteFakeShell("sleep 0.8\nsleep 30 &\nexit 0");
        try
        {
            var timeout = TimeSpan.FromSeconds(1);
            var elapsed = Stopwatch.StartNew();
            var path = StartupPathRepair.ReadLoginShellPath(shell, timeout, NullLogger.Instance);
            elapsed.Stop();

            path.Should().BeNull();

            // Halfway between the one-deadline (~1s) and stacked (~1.8s) outcomes — with the 3s production
            // deadline the same stacking would mean 6s of blocked startup.
            elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(1400));
        }
        finally
        {
            File.Delete(shell);
        }
    }

    [Fact]
    public void UserBinDirectories_CoverTheWellKnownInstallLocations()
    {
        var home = OperatingSystem.IsWindows() ? @"C:\Users\user" : "/home/user";

        IEnumerable<string> expected =
        [
            Path.Combine(home, ".local", "bin"),
            Path.Combine(home, ".bun", "bin"),
            Path.Combine(home, "bin"),
        ];
        if (OperatingSystem.IsMacOS())
        {
            // A Finder launch misses Homebrew's directories the way a Linux GUI launch misses ~/.local/bin.
            expected = expected.Concat(["/opt/homebrew/bin", "/usr/local/bin"]);
        }

        StartupPathRepair.UserBinDirectories(home).Should().Equal(expected);
    }
}
