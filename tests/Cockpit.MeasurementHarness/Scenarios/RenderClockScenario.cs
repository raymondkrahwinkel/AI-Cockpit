using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Threading;
using Cockpit.Core.Diagnostics;
using Cockpit.MeasurementHarness.Core;
using Cockpit.MeasurementHarness.Meters;

namespace Cockpit.MeasurementHarness.Scenarios;

/// <summary>
/// Parks the render clock without touching the UI thread, and checks that the app's own detector sees it.
/// This is trap 2 of AC-1119 — render clock silent, UI thread idle, cpu near zero — and until AC-1169 built
/// it nothing had ever shown that the detector fires at all, so PR #934's evidence had the same shape as the
/// faults this epic is about.
/// </summary>
public static class RenderClockScenario
{
    public const string Name = "render-clock";

    /// <summary>
    /// Parks the render thread. <c>ICustomDrawOperation.Render</c> runs on the render thread, not on the
    /// dispatcher, so sleeping in it stops the compositor committing while the dispatcher carries on — which
    /// is exactly what tells this fault apart from a busy UI thread.
    /// </summary>
    private sealed class RenderThreadStopper : Control
    {
        private static volatile bool _parked;

        public static void Park() => _parked = true;

        public static void Release() => _parked = false;

        public override void Render(DrawingContext context) => context.Custom(new SleepOp(new Rect(Bounds.Size)));

        private sealed class SleepOp(Rect bounds) : ICustomDrawOperation
        {
            public Rect Bounds => bounds;

            public void Dispose()
            {
            }

            public bool Equals(ICustomDrawOperation? other) => false;

            public bool HitTest(Point point) => false;

            public void Render(ImmediateDrawingContext context)
            {
                // In slices, so releasing the clock does not have to wait out one long sleep.
                while (_parked)
                {
                    Thread.Sleep(50);
                }
            }
        }
    }

    /// <summary>
    /// The control: parking the render thread has to produce a stall the real decision function agrees with.
    /// Without it, "no stall detected" cannot be told apart from a probe that never ran — which is precisely
    /// what headless does, and what made seven of seven green on a fault that was there.
    /// </summary>
    public static PositiveControl Control(Pump pump) => PositiveControl.Named(
        "parked-render-thread",
        async recorder =>
        {
            var (stalled, _) = await _ParkAndWatchAsync(pump, recorder).ConfigureAwait(true);
            return stalled;
        });

    /// <summary>
    /// Runs the scenario: park, wait past the real threshold, release. The gate on the witness is what stops
    /// this from claiming anything on a platform where the probe never started.
    /// </summary>
    public static async Task RunAsync(MeasurementRun run, Pump pump)
    {
        var witness = new RenderClockWitness();
        var dispatcher = new DispatcherGapMeter();
        var detectedInMeasurement = false;

        await run.MeasureAsync("a parked render clock, with the UI thread left alone", async recorder =>
        {
            dispatcher.Start(TimeSpan.FromMilliseconds(100));
            var (stalled, resumed) = await _ParkAndWatchAsync(pump, recorder, witness).ConfigureAwait(true);
            dispatcher.Stop();
            detectedInMeasurement = stalled;

            recorder.Measure("dispatcher-ticks-during-stall", dispatcher.Ticks, "ticks");
            run.Write($"stall detected: {stalled} · resume detected: {resumed}");
            run.Write(dispatcher.Line("dispatcher during the stall"));
            run.Write($"render clock threshold in use: {RenderClockHeartbeat.StallAfter.TotalSeconds:F0}s (the app's own)");
        }).ConfigureAwait(true);

        // E6. Everything above is about a render clock, so a run that never saw one has nothing to report.
        run.Gate("render clock observed", () => witness.EverReturned, witness.Failure);

        // The scenario exists to show the detector fires. If the measurement pass parked the render thread and
        // still saw no stall, the run disagrees with its own positive control — and a report that says
        // "no stall" while its control says "a stall is detectable" is worse than no report at all.
        //
        // Held as the measurement pass own answer, not as a count over the recorder: the control records into
        // the same recorder and would otherwise satisfy the gate the measurement failed. Same shape as E2.
        run.Gate(
            "the parked clock was detected",
            () => detectedInMeasurement,
            "the render thread was parked past the app's own threshold and no stall was detected; either the "
            + "commit completes without waiting for the draw operation, or the parking did not reach it");

        // The negative control, and it is the whole difference between trap 2 and a busy UI thread: during
        // the stall the dispatcher has to keep ticking. If it stopped too, this measured something else.
        run.Gate(
            "dispatcher kept ticking",
            () => dispatcher.Ticks > 0,
            $"the dispatcher ticked {dispatcher.Ticks} times during the stall, so the UI thread was blocked too "
            + "— that is a different fault from a parked render clock, and this run cannot tell them apart");
    }

    private static async Task<(bool Stalled, bool Resumed)> _ParkAndWatchAsync(Pump pump, Recorder recorder, RenderClockWitness? shared = null)
    {
        var witness = shared ?? new RenderClockWitness();
        var stopper = new RenderThreadStopper();
        var window = new Window
        {
            Width = 500,
            Height = 300,
            WindowDecorations = WindowDecorations.None,
            ShowInTaskbar = false,
            Content = stopper,
            Title = "AC-1131 render clock",
        };

        window.Show();

        // The render thread needs work, or it stands still for the wrong reason and a silent clock says nothing.
        var redraw = new DispatcherTimer(DispatcherPriority.Default) { Interval = TimeSpan.FromMilliseconds(16) };
        redraw.Tick += (_, _) => stopper.InvalidateVisual();
        redraw.Start();

        // A healthy commit first: this is what proves the probe works before anything is broken on purpose.
        witness.Probe();
        await pump.ForAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(true);

        RenderThreadStopper.Park();
        witness.Probe();

        var deadline = RenderClockHeartbeat.StallAfter + TimeSpan.FromSeconds(2);
        await pump.ForAsync(deadline).ConfigureAwait(true);

        var decision = RenderClockHeartbeat.Decide(witness.OutstandingFor, warned: false);
        if (decision.Stalled)
        {
            recorder.Detected("renderclock-stalled", $"outstanding for {witness.OutstandingFor?.TotalSeconds:F1}s");
        }

        RenderThreadStopper.Release();
        await pump.ForAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(true);

        var afterRelease = RenderClockHeartbeat.Decide(witness.OutstandingFor, warned: decision.Stalled);
        if (afterRelease.Resumed)
        {
            recorder.Detected("renderclock-resumed", "the commit came back");
        }

        redraw.Stop();
        window.Close();
        return (decision.Stalled, afterRelease.Resumed);
    }
}
