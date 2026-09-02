using System.Collections.Concurrent;
using Avalonia.Controls;
using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.TestSupport;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1256: <c>uidispatch starved</c> said a layout pass never finishes and never said whose. Three headless
/// reproductions of the reported freeze came back clean, which is what a line that reports only the symptom buys
/// you. These pin the second line that names the elements the starved pass left unfinished.
/// </summary>
/// <remarks>
/// The pair is the point. A report that always prints something would pass the first test on its own and tell an
/// investigator nothing, so the second one starves the thread the same way over a settled tree and requires the
/// line to say so — that is what makes a named element mean "this one", rather than "here is a tree".
/// </remarks>
[Collection("avalonia")]
public sealed class Ac1256StarvedLayoutReportTests
{
    // The real budget is 15s (RenderClockHeartbeat.StallAfter); the seam exists so this runs on a shorter clock.
    private static readonly TimeSpan AlarmAfter = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(25);

    private const string Where = "uilayout dirty";

    /// <summary>
    /// A tree left mid-layout while the dispatcher is starved above it: the report names the control by its path.
    /// </summary>
    [Fact]
    public async Task AStarvedThreadWithAnUnfinishedLayoutPass_NamesTheElementsStillInIt()
    {
        var logger = new _CapturingLogger();
        var service = new DiagnosticsBackgroundService(logger, alarmAfter: AlarmAfter);
        Window? window = null;

        try
        {
            window = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var opened = new Window { Width = 400, Height = 300, Content = new StackPanel { Name = "TheLoop" } };
                opened.Show();
                opened.UpdateLayout();
                return opened;
            });

            service.SetLayoutRoots(() => [window]);

            // Starved above Render, so the pass the invalidation below queues is never served and the tree stays
            // mid-pass. A control that re-dirties itself from LayoutUpdated cannot stand in here: Avalonia detects
            // that one and throws "Infinite layout loop detected", which is AC-1236's case rather than this one.
            using var starver = StarvedDispatcher.Start(DispatcherPriority.Normal);
            service.Start();

            await Dispatcher.UIThread.InvokeAsync(
                () => ((StackPanel)window.Content!).InvalidateMeasure(), DispatcherPriority.Send);

            Assert.True(
                await _Appears(logger, Where),
                "the dispatcher was starved for longer than the alarm and nothing said where");

            var line = logger.Lines.First(entry => entry.Contains(Where, StringComparison.Ordinal));
            Assert.Contains("StackPanel#TheLoop", line, StringComparison.Ordinal);
            Assert.Contains("[measure]", line, StringComparison.Ordinal);
        }
        finally
        {
            service.Dispose();
            await Dispatcher.UIThread.InvokeAsync(() => window?.Close(), DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// The counter-proof: the same starvation over a settled tree. A starved dispatcher is not automatically a
    /// layout loop, and the line has to be able to say so — otherwise the first test proves only that it prints.
    /// </summary>
    [Fact]
    public async Task AStarvedThreadWithNothingInLayout_SaysTheLoopIsNotALayoutPass()
    {
        var logger = new _CapturingLogger();
        var service = new DiagnosticsBackgroundService(logger, alarmAfter: AlarmAfter);
        Window? window = null;

        try
        {
            window = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var opened = new Window { Width = 400, Height = 300, Content = new StackPanel { Name = "Settled" } };
                opened.Show();
                opened.UpdateLayout();
                return opened;
            });

            service.SetLayoutRoots(() => [window]);

            // The same starvation as above, over a tree that settled before it began.
            using var starver = StarvedDispatcher.Start(DispatcherPriority.Normal);
            service.Start();

            Assert.True(await _Appears(logger, Where), "the starvation itself was never reported");

            var line = logger.Lines.First(entry => entry.Contains(Where, StringComparison.Ordinal));
            Assert.Contains("not a layout pass", line, StringComparison.Ordinal);
            Assert.DoesNotContain("Settled", line, StringComparison.Ordinal);
        }
        finally
        {
            service.Dispose();
            await Dispatcher.UIThread.InvokeAsync(() => window?.Close(), DispatcherPriority.Background);
        }
    }

    // AFreezeThatGoesOn_IsAskedMoreThanOnce stood here, waiting for "sample=1/" and "sample=2/". LayoutLoopGuard-
    // Tests.WithTheSwitchOff_TheSameStandstillIsSampledAndLeftAlone waits for "sample=3/" over the same
    // starvation: strictly more, same counter, same log statement, and on 1s samples where this sat out 10s.

    private static async Task<bool> _Appears(_CapturingLogger logger, string token)
    {
        var deadline = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < deadline)
        {
            if (logger.Lines.Any(line => line.Contains(token, StringComparison.Ordinal)))
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    // Concurrent for the same reason Ac1196FreezeDetectorTests' own is: the background thread writes while the
    // poll above reads.
    private sealed class _CapturingLogger : ILogger<DiagnosticsBackgroundService>
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IEnumerable<string> Lines => _lines;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => _NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _lines.Enqueue(formatter(state, exception));

        private sealed class _NullScope : IDisposable
        {
            public static readonly _NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
