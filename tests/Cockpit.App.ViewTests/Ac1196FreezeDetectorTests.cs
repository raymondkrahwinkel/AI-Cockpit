using System.Collections.Concurrent;
using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.TestSupport;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1196: the detector reports both freezes it used to miss, from the background thread, and reports them as two
/// different things. Four reproduced freezes logged <c>hang=1</c> with zero stalls; these are why.
/// </summary>
/// <remarks>
/// <b>Why they could not fire before.</b> The probe's clock was stamped by the probe itself, on the UI thread, so a
/// thread that never ran it left the reading empty — and empty is the healthy case by contract. Every observation
/// here is now taken on the background thread, from a stamp taken at the post (T4).
/// <para>
/// <b>T1 and T2 are different faults, not one fault twice.</b> T1 holds the thread with one non-yielding job: nothing
/// runs, and the commit can never be requested, so the render clock owes the answer. T2 keeps the thread pumping at a
/// priority above the probe's — the app draws, takes input, and the clock ticks — so calling it a renderclock stall
/// would be a wrong diagnosis. It gets its own <c>uidispatch starved</c> line instead (T3).
/// </para>
/// <para>
/// <b>T7 is what makes a green T1/T2 mean anything.</b> Without a quiet run in the same file, a detector that alarmed
/// on everything would pass both.
/// </para>
/// </remarks>
[Collection("avalonia")]
public sealed class Ac1196FreezeDetectorTests
{
    // The real budget is RenderClockHeartbeat.StallAfter (15s). Held hostage for that long, three times over, this
    // file would spin a core for a minute — the seam exists so the rule under test runs on a shorter clock.
    private static readonly TimeSpan AlarmAfter = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    private const string Stalled = "renderclock stalled";
    private const string Starved = "uidispatch starved";

    /// <summary>T1 — one non-yielding job on Default: nothing runs, and that is the render clock's to answer for.</summary>
    [Fact]
    public Task ABlockedUiThread_IsFinallyReportedAsARenderClockStall() =>
        _ABlockIsTheRenderClocksToAnswerFor(dispatcherBusyFirst: false);

    /// <summary>AC-1255 — the same block behind a busy dispatcher, the arrangement that let the pong land first.</summary>
    [Fact]
    public Task ABlockedUiThreadThatAnsweredOnce_IsStillNotCalledStarvation() =>
        _ABlockIsTheRenderClocksToAnswerFor(dispatcherBusyFirst: true);

    /// <summary>T2 — starved at Render (4), the priority a runaway render pass reposts at.</summary>
    [Fact]
    public Task AStarvedUiThreadAtRender_IsReportedAsDispatchStarvation() =>
        _StarvationIsItsOwnAlarm(DispatcherPriority.Render);

    /// <summary>T2 — the same at Loaded (1), one step above the Background the probe is posted at.</summary>
    [Fact]
    public Task AStarvedUiThreadAtLoaded_IsReportedAsDispatchStarvation() =>
        _StarvationIsItsOwnAlarm(DispatcherPriority.Loaded);

    /// <summary>T7 — the silent positive control: a quiet thread earns neither alarm, in the same run.</summary>
    [Fact]
    public async Task AQuietUiThread_RaisesNeitherAlarm()
    {
        var logger = new _CapturingLogger();
        var service = new DiagnosticsBackgroundService(logger, alarmAfter: AlarmAfter);

        try
        {
            service.Start();

            // Armed on the short budget, and long enough past it that a detector which alarms on anything would
            // have. Without this wait the run would prove only that three seconds is longer than nothing.
            Assert.True(await _Appears(logger, $"stallAfter={AlarmAfter.TotalSeconds:0}s"));
            await Task.Delay(AlarmAfter * 3);

            Assert.DoesNotContain(logger.Lines, line => line.Contains(Stalled, StringComparison.Ordinal));
            Assert.DoesNotContain(logger.Lines, line => line.Contains(Starved, StringComparison.Ordinal));
            Assert.False(service.RenderersShouldPause);
        }
        finally
        {
            service.Dispose();
        }
    }

    private static async Task _ABlockIsTheRenderClocksToAnswerFor(bool dispatcherBusyFirst)
    {
        using var blocked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var logger = new _CapturingLogger();
        var service = new DiagnosticsBackgroundService(logger, alarmAfter: AlarmAfter);

        try
        {
            if (dispatcherBusyFirst)
            {
                // Inert in the passing path: here only to make this case go red if the wait below is ever dropped.
                Dispatcher.UIThread.Post(() => Thread.Sleep(300), DispatcherPriority.Send);
            }

            // Queued before the service starts, so its very first probe is the one that never gets picked up. A
            // Wait, not a Sleep: the block ends when this test says so and cannot run on into the next one.
            Dispatcher.UIThread.Post(
                () =>
                {
                    blocked.Set();
                    release.Wait(Deadline);
                },
                DispatcherPriority.Default);

            // AC-1255: signalled, not timed — swap this wait for a delay and the Send pong outranks the still-queued
            // block, lands, and dates a dead thread as one that just pumped.
            Assert.True(blocked.Wait(Deadline), "the blocking job never reached the UI thread");
            service.Start();

            Assert.True(await _Appears(logger, Stalled), $"blocked for over {AlarmAfter.TotalSeconds:0}s and nothing was reported");
            Assert.DoesNotContain(logger.Lines, line => line.Contains(Starved, StringComparison.Ordinal));

            // AC-883's gate stays where it was: a thread that cannot render is not helped by being told to stop.
            Assert.False(service.RenderersShouldPause);
        }
        finally
        {
            release.Set();
            service.Dispose();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }

    private static async Task _StarvationIsItsOwnAlarm(DispatcherPriority priority)
    {
        var logger = new _CapturingLogger();
        var service = new DiagnosticsBackgroundService(logger, alarmAfter: AlarmAfter);

        // Started first, so the service's first probe is starved from the moment it is posted rather than ten
        // seconds later. The Send pong outranks this loop, so it lands anyway — which is what says "pumping".
        using var starver = StarvedDispatcher.Start(priority);

        try
        {
            service.Start();

            Assert.True(await _Appears(logger, Starved), $"starved at {priority} and the background loop said nothing");

            // T3: the whole point. The clock is ticking and the app is drawing, so reporting a stopped render
            // clock here would send the next round after the wrong thing, exactly as this one was.
            Assert.DoesNotContain(logger.Lines, line => line.Contains(Stalled, StringComparison.Ordinal));
            Assert.False(service.RenderersShouldPause);

            Assert.True(starver.Rounds > 10, $"the thread has to have kept working, not blocked; rounds={starver.Rounds}");
        }
        finally
        {
            service.Dispose();
            starver.Dispose();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }

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

    // Concurrent, not a List: the background thread writes while the poll above reads, which is the whole shape of
    // these tests. Its own class rather than the one in DiagnosticsBackgroundServiceTests, which is not thread-safe.
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
