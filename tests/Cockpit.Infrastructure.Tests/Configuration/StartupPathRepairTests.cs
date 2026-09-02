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

    // AC-610: a freshly written script can answer execve() with ETXTBSY for a moment after its own write handle
    // closes (confirmed in ReadLoginShellPath_WhenTheScriptIsBusyForWriting_...). Probing with a real exec-and-kill
    // clears that race instead of guessing at a fixed delay.
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

    // One predicate, one test: an entry counts only when a whole PATH entry normalises to the wanted directory.
    // The rows are the input classes that used to be a Fact each — present, absent, a trailing slash on either
    // side, a prefix that must not count, and an empty PATH. Built through Join so the separator stays the host's.
    public static IEnumerable<object[]> ContainsEntryCases() =>
    [
        [Join("/usr/local/bin", "/home/user/.local/bin", "/usr/bin"), "/home/user/.local/bin", true],
        [Join("/usr/local/bin", "/usr/bin"), "/home/user/.local/bin", false],
        ["/home/user/.local/bin/", "/home/user/.local/bin", true],
        ["/home/user/.local/bin", "/home/user/.local/bin/", true],
        ["/usr/local", "/usr/local/bin", false],
        ["", "/usr/local/bin", false],
    ];

    [Theory]
    [MemberData(nameof(ContainsEntryCases))]
    public void ContainsEntry_MatchesAWholeEntryOnly(string path, string directory, bool expected) =>
        Assert.Equal(expected, StartupPathRepair.ContainsEntry(path, directory));

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

    // The marker line is pulled out of whatever the shell printed around it. Rows, because this is one function
    // over four input classes: a noisy motd, an init that echoes the unexpanded probe first (`set -x`), output with
    // no marker at all, and a marker with nothing after it. Built through MemberData so Marker stays a runtime read.
    public static IEnumerable<object[]> ExtractMarkedPathCases() =>
    [
        [$"Welcome to Fedora!\nsome motd line\n{StartupPathRepair.Marker}/usr/local/bin:/usr/bin\n", "/usr/local/bin:/usr/bin"],
        [$"+ echo {StartupPathRepair.Marker}$PATH\n{StartupPathRepair.Marker}/usr/bin\n", "/usr/bin"],
        ["login: something went wrong\n", null!],
        [$"{StartupPathRepair.Marker}\n", null!],
    ];

    [Theory]
    [MemberData(nameof(ExtractMarkedPathCases))]
    public void ExtractMarkedPath_TakesTheLastMarkerLine_OrNothingWhenThereIsNoUsableOne(string output, string? expected) =>
        Assert.Equal(expected, StartupPathRepair.ExtractMarkedPath(output));

    [PosixFact("Reading the PATH from a login shell is the POSIX repair; Windows edits the registry instead.")]
    public void ReadLoginShellPath_FromAnAnsweringShell_ReturnsItsMarkedPath()
    {
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

    [PosixFact("Reading the PATH from a login shell is the POSIX repair; Windows edits the registry instead.")]
    public void ReadLoginShellPath_WhenTheShellWedges_GivesUpWithinTheDeadline()
    {
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

    [PosixFact("Reading the PATH from a login shell is the POSIX repair; Windows edits the registry instead.")]
    public void ReadLoginShellPath_WhenABackgroundChildHoldsTheStdoutPipe_IsBoundedByOneDeadlineNotTwo()
    {
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

    [PosixFact("Reading the PATH from a login shell is the POSIX repair; Windows edits the registry instead.")]
    public void ReadLoginShellPath_WhenTheShellExitsWithoutAMarkerLine_RecordsTheExitCodeAndStderr()
    {
        // A shell that answers and exits cleanly but whose init errored before the PATH echo. The sleep isn't
        // about the production timeout — the stderr read is strictly non-blocking, so this just gives it real
        // wall-clock time to finish before the assertion checks it.
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

    [PosixFact("Reading the PATH from a login shell is the POSIX repair; Windows edits the registry instead.")]
    public void ReadLoginShellPath_WhenTheScriptIsBusyForWriting_RecordsTheExceptionTypeAndMessage()
    {
        // Linux refuses execve on a file still open for writing (AC-610's ETXTBSY hypothesis). WriteFakeShell
        // already probes past that race, so this test induces it directly instead — holding the write handle
        // open — to confirm the catch branch records what happened.
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
