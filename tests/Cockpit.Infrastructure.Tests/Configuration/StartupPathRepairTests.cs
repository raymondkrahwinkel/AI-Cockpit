using System.ComponentModel;
using System.Diagnostics;
using Cockpit.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

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
        var path = _WriteExecutableScript(body);
        if (!OperatingSystem.IsWindows())
        {
            _WaitUntilExecReady(path);
        }

        return path;
    }

    private static string _WriteExecutableScript(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cockpit-fake-shell-{Guid.NewGuid():N}.sh");
        File.WriteAllText(path, $"#!/bin/sh\n{body}\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    // AC-610: confirmed by artificially holding a write handle open across an exec (see
    // ReadLoginShellPath_WhenTheScriptIsBusyForWriting_...) — a freshly written script can answer execve() with
    // ETXTBSY (errno 26, "Text file busy") for a moment after its own write handle is closed. Probing with a real
    // exec-and-kill clears the same race instead of guessing at a fixed delay before returning the path.
    private static void _WaitUntilExecReady(string path)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var probe = Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                try
                {
                    probe?.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already exited on its own (a fast script can finish before Kill reaches it) — nothing to clean up.
                }

                probe?.WaitForExit();

                return;
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 26 && deadline.Elapsed < TimeSpan.FromSeconds(2))
            {
                Thread.Sleep(5);
            }
        }
    }

    private sealed class _CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => _NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));

        private sealed class _NullScope : IDisposable
        {
            public static readonly _NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    [Fact]
    public void ContainsEntry_WhenTheDirectoryIsOnThePath_IsTrue()
    {
        var path = Join("/usr/local/bin", "/home/user/.local/bin", "/usr/bin");

        Assert.True(StartupPathRepair.ContainsEntry(path, "/home/user/.local/bin"));
    }

    [Fact]
    public void ContainsEntry_WhenTheDirectoryIsMissing_IsFalse()
    {
        var path = Join("/usr/local/bin", "/usr/bin");

        Assert.False(StartupPathRepair.ContainsEntry(path, "/home/user/.local/bin"));
    }

    [Fact]
    public void ContainsEntry_ToleratesATrailingSlashOnEitherSide()
    {
        Assert.True(StartupPathRepair.ContainsEntry("/home/user/.local/bin/", "/home/user/.local/bin"));
        Assert.True(StartupPathRepair.ContainsEntry("/home/user/.local/bin", "/home/user/.local/bin/"));
    }

    [Fact]
    public void ContainsEntry_DoesNotMatchAPrefixEntry()
    {
        // "/usr/local" on PATH must not count as "/usr/local/bin" being on it — entries match whole, not by prefix.
        Assert.False(StartupPathRepair.ContainsEntry("/usr/local", "/usr/local/bin"));
    }

    [Fact]
    public void ContainsEntry_OnAnEmptyPath_IsFalse()
    {
        Assert.False(StartupPathRepair.ContainsEntry(string.Empty, "/home/user/.local/bin"));
    }

    [Fact]
    public void MergePaths_PutsTheLoginShellEntriesFirst_AndKeepsTheCurrentOnlyOnes()
    {
        // The truncated PATH carries entries the login shell does not know (the AppImage mount, dotnet tools);
        // those must survive the merge, after the shell's own ordering.
        var loginShell = Join("/home/user/.local/bin", "/usr/local/bin", "/usr/bin");
        var current = Join("/tmp/.mount_cockpit", "/usr/local/bin", "/usr/bin", "/home/user/.dotnet/tools");

        var merged = StartupPathRepair.MergePaths(loginShell, current);

        Assert.Equal(Join(
            "/home/user/.local/bin", "/usr/local/bin", "/usr/bin", "/tmp/.mount_cockpit", "/home/user/.dotnet/tools"), merged);
    }

    [Fact]
    public void MergePaths_DeduplicatesATrailingSlashVariant()
    {
        var loginShell = "/usr/bin/";
        var current = "/usr/bin";

        Assert.Equal("/usr/bin/", StartupPathRepair.MergePaths(loginShell, current));
    }

    [Fact]
    public void PrependMissingEntries_PrependsOnlyTheDirectoriesNotAlreadyOnThePath()
    {
        var path = Join("/home/user/.local/bin", "/usr/bin");

        var repaired = StartupPathRepair.PrependMissingEntries(path, ["/home/user/.local/bin", "/home/user/.bun/bin"]);

        Assert.Equal(Join("/home/user/.bun/bin", "/home/user/.local/bin", "/usr/bin"), repaired);
    }

    [Fact]
    public void PrependMissingEntries_WhenEverythingIsAlreadyThere_LeavesThePathUntouched()
    {
        var path = Join("/home/user/.local/bin", "/usr/bin");

        Assert.Equal(path, StartupPathRepair.PrependMissingEntries(path, ["/home/user/.local/bin"]));
    }

    [Fact]
    public void PrependMissingEntries_OnAnEmptyPath_YieldsJustTheDirectories()
    {
        Assert.Equal("/home/user/bin", StartupPathRepair.PrependMissingEntries(string.Empty, ["/home/user/bin"]));
    }

    [Fact]
    public void ExtractMarkedPath_PullsThePathOutOfNoisyShellOutput()
    {
        var output = $"Welcome to Fedora!\nsome motd line\n{StartupPathRepair.Marker}/usr/local/bin:/usr/bin\n";

        Assert.Equal("/usr/local/bin:/usr/bin", StartupPathRepair.ExtractMarkedPath(output));
    }

    [Fact]
    public void ExtractMarkedPath_WhenAnInitEchoesTheProbe_TakesTheLastMarkerLine()
    {
        // An init with `set -x` (or an echoing plugin) prints the unexpanded probe before the real answer.
        var output = $"+ echo {StartupPathRepair.Marker}$PATH\n{StartupPathRepair.Marker}/usr/bin\n";

        Assert.Equal("/usr/bin", StartupPathRepair.ExtractMarkedPath(output));
    }

    [Fact]
    public void ExtractMarkedPath_WithoutAMarkerLine_IsNull()
    {
        Assert.Null(StartupPathRepair.ExtractMarkedPath("login: something went wrong\n"));
    }

    [Fact]
    public void ExtractMarkedPath_WithAnEmptyValue_IsNull()
    {
        Assert.Null(StartupPathRepair.ExtractMarkedPath($"{StartupPathRepair.Marker}\n"));
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
            var log = new _CapturingLogger();

            var path = StartupPathRepair.ReadLoginShellPath(shell, TimeSpan.FromSeconds(5), log);

            // A future CI flake here should explain itself instead of just showing "expected X, got null" — the
            // capturing logger's recorded reason (the branch, the exit code/stderr or the exception) rides along.
            Assert.True(path == "/fake/login/bin:/usr/bin",
                $"""expected "/fake/login/bin:/usr/bin" but got {(path is null ? "null" : $"\"{path}\"")}; recorded: {(log.Messages.Count == 0 ? "(nothing logged)" : string.Join(" | ", log.Messages))}""");
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
            var log = new _CapturingLogger();
            var elapsed = Stopwatch.StartNew();
            var path = StartupPathRepair.ReadLoginShellPath(shell, TimeSpan.FromMilliseconds(500), log);
            elapsed.Stop();

            Assert.Null(path);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5));
            Assert.Contains(log.Messages, message => message.Contains("did not answer within", StringComparison.Ordinal));
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
            var log = new _CapturingLogger();
            var timeout = TimeSpan.FromSeconds(1);
            var elapsed = Stopwatch.StartNew();
            var path = StartupPathRepair.ReadLoginShellPath(shell, timeout, log);
            elapsed.Stop();

            Assert.Null(path);

            // Halfway between the one-deadline (~1s) and stacked (~1.8s) outcomes — with the 3s production
            // deadline the same stacking would mean 6s of blocked startup.
            Assert.True(elapsed.Elapsed < TimeSpan.FromMilliseconds(1400));
            Assert.Contains(log.Messages, message => message.Contains("did not finish within the remaining", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(shell);
        }
    }

    [Fact]
    public void ReadLoginShellPath_WhenTheShellExitsWithoutAMarkerLine_RecordsTheExitCodeAndStderr()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // A shell that answers and exits cleanly, but whose init errored before ever reaching the PATH echo. The
        // small sleep before exit is not about the production timeout — ReadLoginShellPath's stderr read is
        // strictly non-blocking, so this just gives that background read real wall-clock time to finish before
        // the assertion checks it, the same way a slower real shell init would.
        var shell = WriteFakeShell("echo boom 1>&2\nsleep 0.05\nexit 7");
        try
        {
            var log = new _CapturingLogger();

            var path = StartupPathRepair.ReadLoginShellPath(shell, TimeSpan.FromSeconds(5), log);

            Assert.Null(path);
            Assert.Contains(log.Messages, message =>
                message.Contains("exit 7", StringComparison.Ordinal) && message.Contains("boom", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(shell);
        }
    }

    [Fact]
    public void ReadLoginShellPath_WhenTheScriptIsBusyForWriting_RecordsTheExceptionTypeAndMessage()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // ETXTBSY is a Linux execve rule; Windows has no equivalent restriction.
        }

        // The ETXTBSY hypothesis (AC-610): Linux refuses execve on a file that is still open for writing.
        // WriteFakeShell already probes past that race (see _WaitUntilExecReady) before handing back a path, so
        // this test induces the race directly instead — by holding the script's own write handle open — to
        // confirm ReadLoginShellPath's catch branch records what actually happened.
        var shell = _WriteExecutableScript("echo \"__COCKPIT_LOGIN_PATH__=/fake/login/bin:/usr/bin\"");
        try
        {
            using var busy = new FileStream(shell, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            var log = new _CapturingLogger();

            var path = StartupPathRepair.ReadLoginShellPath(shell, TimeSpan.FromSeconds(5), log);

            Assert.Null(path);
            Assert.Contains(log.Messages, message =>
                message.Contains(nameof(Win32Exception), StringComparison.Ordinal) &&
                message.Contains("busy", StringComparison.OrdinalIgnoreCase));
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

        Assert.Equal(expected, StartupPathRepair.UserBinDirectories(home));
    }
}
