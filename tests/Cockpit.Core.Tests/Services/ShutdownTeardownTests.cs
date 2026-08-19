using System.Diagnostics;
using System.Text.RegularExpressions;
using Cockpit.App;
using Cockpit.App.Logging;
using Microsoft.Extensions.Logging;

namespace Cockpit.Core.Tests.Services;

/// <summary>
/// The shutdown teardown (AC-958). It used to run from <c>Program.Main</c>'s <c>finally</c>, after Avalonia's main
/// loop had ended: every await in the chain posted a continuation nobody ran, so the teardown stopped halfway and
/// left the session temp files behind — without a single line in the log to say so. What is pinned here is the part
/// that made it invisible and the part that keeps the exit bounded; the "run it while the dispatcher lives" half is a
/// wiring decision, guarded below by reading the source rather than by running a desktop lifetime in a unit test.
/// </summary>
public partial class ShutdownTeardownTests
{
    [Fact]
    public async Task AwaitTeardownAsync_GivesUpOnAWedgedTeardownAndSaysSo()
    {
        var log = _CaptureLifecycleLog();
        var budget = TimeSpan.FromMilliseconds(150);
        var wedged = new TaskCompletionSource().Task;

        var elapsed = Stopwatch.StartNew();
        await Program.AwaitTeardownAsync(wedged, budget);
        elapsed.Stop();

        // Bounded: bug #32's rule is that the exit never waits on a teardown that is not coming back. The margin over
        // the budget is wide on purpose — without the bound this hangs for good, so slow is not the failure mode.
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"waited {elapsed.Elapsed} on a teardown that never finishes");
        Assert.Contains(log.Messages, message => message.Contains("did not finish", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AwaitTeardownAsync_LogsATeardownThatThrows()
    {
        var log = _CaptureLifecycleLog();

        await Program.AwaitTeardownAsync(Task.FromException(new InvalidOperationException("session teardown blew up")), TimeSpan.FromSeconds(5));

        Assert.Contains(log.Messages, message => message.Contains("session teardown blew up", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AwaitTeardownAsync_SaysNothingWhenTheTeardownFinishes()
    {
        var log = _CaptureLifecycleLog();

        await Program.AwaitTeardownAsync(Task.CompletedTask, TimeSpan.FromSeconds(5));

        Assert.DoesNotContain(log.Messages, message =>
            message.Contains("did not finish", StringComparison.Ordinal) || message.Contains("teardown failed", StringComparison.Ordinal));
    }

    /// <summary>
    /// The lifetime's <c>Shutdown()</c> is a forced shutdown that never raises <c>ShutdownRequested</c>, so a quit
    /// route calling it directly skips the teardown entirely — which is how the tray's Quit and the restart handoff
    /// behaved before this. One call site, in the method that has just awaited the teardown; a second one appearing
    /// anywhere in the app means a quit route has grown its own way out again.
    /// </summary>
    [Fact]
    public void TheAppShutsTheLifetimeDownInExactlyOnePlace()
    {
        var appDirectory = _LocateRepositoryFolder(Path.Combine("src", "Cockpit.App"))
            ?? throw new InvalidOperationException("No src/Cockpit.App directory above the test output — this test reads the repo it belongs to.");

        var sources = Directory.EnumerateFiles(appDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        Assert.True(sources.Count > 50, "the app has well over fifty source files — finding almost none means the walk broke, not that the rule holds");

        var callers = sources
            .Select(path => (Path: Path.GetRelativePath(appDirectory, path).Replace(Path.DirectorySeparatorChar, '/'),
                Count: LifetimeShutdownRegex().Count(File.ReadAllText(path))))
            .Where(file => file.Count > 0)
            .ToList();

        Assert.Equal(new[] { ("App.axaml.cs", 1) }, callers);
    }

    /// <summary>A <c>Shutdown()</c> on the desktop lifetime, whatever the variable holding it is called.</summary>
    [GeneratedRegex(@"(desktop|lifetime)\??\.Shutdown\(", RegexOptions.IgnoreCase)]
    private static partial Regex LifetimeShutdownRegex();

    private static CapturingLogger<Program> _CaptureLifecycleLog()
    {
        var logger = new CapturingLogger<Program>();
        LifecycleLog.Use(new _SingleLoggerFactory(logger));

        return logger;
    }

    private sealed class _SingleLoggerFactory(ILogger logger) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => logger;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }

    private static string? _LocateRepositoryFolder(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
