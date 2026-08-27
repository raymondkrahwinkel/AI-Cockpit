using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.App.Controls;
using Cockpit.MeasurementHarness.Core;
using Cockpit.MeasurementHarness.Meters;

namespace Cockpit.MeasurementHarness.Scenarios;

/// <summary>
/// Sweeps the session count over the real <see cref="SessionTilePanel"/> in focus+rail and counts the layout
/// rounds each frame needs. Avalonia cuts a frame off at 153 rounds, and AC-1178 measured that cut-off
/// appearing from five sessions on — with two to four as the negative control on the same binary.
/// </summary>
public static class LayoutLoopScenario
{
    public const string Name = "layout-loop";

    /// <summary>Avalonia's own limit on layout rounds in one frame; reaching it is the fault, not a symptom of it.</summary>
    public const int AvaloniaRoundLimit = 153;

    /// <summary>
    /// The control: a control that invalidates its own measure from LayoutUpdated. Built the same way the
    /// real fault is — invalidating from the end of a pass, while the media context is still draining its
    /// render callbacks, which is exactly where ScrollChanged fires too.
    /// </summary>
    private sealed class SelfInvalidating : Control
    {
        private int _passes;

        public SelfInvalidating() => LayoutUpdated += (_, _) => InvalidateMeasure();

        protected override Size MeasureOverride(Size availableSize) => new(10, 20 + (++_passes % 3));
    }

    /// <summary>Fewer frames than this cannot say anything about the worst frame, so the run refuses to try.</summary>
    public const int MinimumFramesPerPoint = 30;

    /// <summary>The positive control, as a value the run cannot be constructed without.</summary>
    public static PositiveControl Control(Pump pump, int settleMs) => PositiveControl.Named(
        "self-invalidating-child",
        async recorder =>
        {
            var before = recorder.DetectorCount("layout-loop");
            var window = _NewWindow(400, 300);
            window.Content = new SelfInvalidating();
            window.Show();
            await pump.ForAsync(TimeSpan.FromMilliseconds(settleMs)).ConfigureAwait(true);
            window.Close();
            return recorder.DetectorCount("layout-loop") > before;
        });

    /// <summary>
    /// Runs the sweep. Every session count gets its own window so no state carries over, and the heaviest
    /// action AC-1178 found — switching which pane has focus — is exercised at each one.
    /// </summary>
    public static async Task SweepAsync(MeasurementRun run, Pump pump, SweepOptions options)
    {
        var points = new List<SeriesPoint>();
        var thinnest = int.MaxValue;

        for (var sessions = options.MinSessions; sessions <= options.MaxSessions; sessions++)
        {
            var count = sessions;
            await run.MeasureAsync($"{count} sessions in focus+rail at {options.Width}x{options.Height}", async recorder =>
            {
                var before = recorder.DetectorCount("layout-loop");
                var frames = new FrameMeter();
                var window = _NewWindow(options.Width, options.Height);
                var panel = _NewPanel(count);
                window.Content = panel;
                window.Show();
                frames.Attach(window);

                var settle = TimeSpan.FromMilliseconds(options.SettleMs);
                await pump.ForAsync(settle).ConfigureAwait(true);
                _SwitchFocus(panel);
                await pump.ForAsync(settle).ConfigureAwait(true);

                var loops = recorder.DetectorCount("layout-loop") - before;
                var worst = frames.MaxRoundsInAFrame();
                thinnest = Math.Min(thinnest, frames.FrameCount);
                window.Close();

                recorder.Measure($"layout-loops@{count}", loops, "exceptions");
                if (worst is { } rounds)
                {
                    recorder.Measure($"rounds-worst-frame@{count}", rounds, "rounds");
                    points.Add(new SeriesPoint(count, rounds));
                }

                run.Write($"{count,3} sessions | worst frame {(worst is { } w ? $"{w,4}" : " n/a")} rounds "
                          + $"| {frames.TotalRounds,6} rounds total | {frames.FrameCount,5} frames | layout loops {loops}"
                          + (worst >= AvaloniaRoundLimit ? "   <-- at Avalonia's cut-off" : string.Empty));
            }).ConfigureAwait(true);
        }

        // The central figure of this sweep is rounds per frame, so a sweep point without a frame clock is a
        // hole in it rather than a low reading. Headless without forced render ticks produces exactly that.
        var expected = options.MaxSessions - options.MinSessions + 1;
        run.Gate(
            "frame clock",
            () => points.Count == expected,
            $"{points.Count} of {expected} sweep points had a frame clock, so rounds per frame cannot be compared across the sweep");

        // A pulse is not the same as enough of a pulse. A handful of frames per point produces a low
        // rounds-per-frame figure that reads exactly like a healthy app, which is the failure this whole
        // epic is about — so the thin run is refused instead of reported.
        run.Gate(
            "frames per sweep point",
            () => thinnest >= MinimumFramesPerPoint,
            $"the thinnest sweep point saw {thinnest} frames, under the {MinimumFramesPerPoint} this figure needs; "
            + "raise --settle-ms, or run without --headless");

        // E5: more sessions may cost more rounds or the same, never fewer. 7452 rounds at 15 tiles against
        // 3146 at 20 stood in two reports for half a day because nothing put the numbers side by side.
        run.Shape(Series.Monotonic("sessions -> worst frame rounds", points));
    }

    private static Window _NewWindow(double width, double height) => new()
    {
        Width = width,
        Height = height,
        WindowDecorations = WindowDecorations.None,
        ShowInTaskbar = false,
        Title = "AC-1131 measurement harness",
    };

    private static SessionTilePanel _NewPanel(int sessions)
    {
        var panel = new SessionTilePanel { FocusRailLayout = true };
        for (var i = 0; i < sessions; i++)
        {
            var tile = new Border
            {
                Background = Brushes.DimGray,
                Child = new TextBlock { Text = $"pane {i}", TextWrapping = TextWrapping.Wrap },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };

            SessionTilePanel.SetRailSortKey(tile, i);
            SessionTilePanel.SetIsFocusCandidate(tile, i == 0);
            panel.Children.Add(tile);
        }

        return panel;
    }

    private static void _SwitchFocus(SessionTilePanel panel)
    {
        if (panel.Children.Count < 2)
        {
            return;
        }

        SessionTilePanel.SetIsFocusCandidate((Control)panel.Children[0], false);
        SessionTilePanel.SetIsFocusCandidate((Control)panel.Children[^1], true);
    }
}

/// <summary>What the sweep varies and what it holds fixed.</summary>
public sealed record SweepOptions(int MinSessions, int MaxSessions, double Width, double Height, int SettleMs);
