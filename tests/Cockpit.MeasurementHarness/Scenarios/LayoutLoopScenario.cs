using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
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

    private const double MinimumPeerFraction = 0.25;

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
        var thinnest = int.MaxValue;
        var blind = new List<string>();
        var allPoints = new List<SeriesPoint>();
        var passPoints = new List<IReadOnlyList<SeriesPoint>>();

        // AC-1178 repeated its threshold three times per session count, and so should anything claiming to
        // reproduce it. Repeats live inside one run because the report identity is the argv (E4): a second
        // process with the same flags is refused, which is right for evidence and useless for repetition.
        for (var pass = 1; pass <= options.Repeats; pass++)
        {
        var points = new List<SeriesPoint>();

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
                    allPoints.Add(new SeriesPoint(count, rounds));
                }

                // AC-1220's independent witness: the round counter reaches Avalonia's limit without reading a
                // single character of its message, so it can say a frame was cut off even when the text match no
                // longer can. The two disagreeing is the break, and the sweep refuses rather than reports zero.
                if (worst >= AvaloniaRoundLimit && loops == 0)
                {
                    blind.Add($"pass {pass} at {count} sessions ({worst} rounds)");
                }

                run.Write($"pass {pass} | {count,3} sessions | worst frame {(worst is { } w ? $"{w,4}" : " n/a")} rounds "
                          + $"| {frames.TotalRounds,6} rounds total | {frames.FrameCount,5} frames | layout loops {loops}"
                          + (worst >= AvaloniaRoundLimit ? "   <-- at Avalonia's cut-off" : string.Empty));

                // AC-1104 asks what a non-converging pass costs, not only that it happens. Only the points that
                // reach the cut-off have that price to report; below it there is nothing to price.
                if (frames.CostOfFramesAtOrAbove(AvaloniaRoundLimit) is { } cost)
                {
                    recorder.Measure($"cut-off-frames@{count}", cost.Frames, "frames");
                    recorder.Measure($"cut-off-longest-ms@{count}", (long)cost.LongestMs, "ms");
                    recorder.Measure($"cut-off-bytes-per-round@{count}", cost.AllocatedBytesPerRound, "bytes");
                    run.Write($"         cost | {cost.Line($"{count} sessions at the cut-off")}");
                }
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

        run.Write(Series.Monotonic($"pass {pass}: sessions -> worst frame rounds", points).Line);
        passPoints.Add(points);
        }

        run.Gate(
            "cut-off frames were recognised as cut-offs",
            () => blind.Count == 0,
            $"{string.Join("; ", blind)} ran into Avalonia's {AvaloniaRoundLimit}-round cut-off and produced no "
            + "cut-off the app recognised, so this sweep cannot tell a working detector from a broken one");

        // E5: more sessions may cost more rounds or the same, never fewer. 7452 rounds at 15 tiles against
        // 3146 at 20 stood in two reports for half a day because nothing put the numbers side by side.
        // It judges the sweep median, not noise in one pass; a real declining median still fails.
        run.Shape(Series.Monotonic(
            "sessions -> median worst frame rounds across passes",
            Series.MedianByX(allPoints)));
        run.Shape(Series.Magnitude(
            "sessions -> worst frame rounds per pass",
            passPoints,
            MinimumPeerFraction));
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
            var pane = _NewPane(i);
            SessionTilePanel.SetIsFocusCandidate(pane, i == 0);
            panel.Children.Add(pane);
        }

        return panel;
    }

    /// <summary>
    /// One pane, shaped like the real one in `CockpitView.axaml`: a `PaneRoot` container the panel writes its
    /// attached boxes onto, with a <see cref="MiniatureHost"/> inside reading them back. **That path is the
    /// amplifier** — `SessionTilePanel` writes `MiniatureFocusChildBox` from inside its own arrange, the host
    /// binds it, and `FocusChildBoxProperty` is registered `AffectsMeasure`, so every arrange leaves measure
    /// dirty again. A plain border in this place measures a healthy app that does not exist.
    /// </summary>
    private static Control _NewPane(int index)
    {
        var content = _NewRealTranscript(index);
        var host = new MiniatureHost { Child = content };
        var paneRoot = new Grid { Margin = new Thickness(4), Background = Brushes.Transparent };
        paneRoot.Children.Add(host);

        // The three bindings of the real markup, by observable rather than by element name. The panel writes
        // onto the container it already holds; the host is what turns those boxes into a layout.
        host.Bind(MiniatureHost.TileSizeProperty, paneRoot.GetObservable(SessionTilePanel.MiniatureTileSizeProperty));
        host.Bind(MiniatureHost.FocusSizeProperty, paneRoot.GetObservable(SessionTilePanel.MiniatureFocusSizeProperty));
        host.Bind(MiniatureHost.FocusChildBoxProperty, paneRoot.GetObservable(SessionTilePanel.MiniatureFocusChildBoxProperty));

        SessionTilePanel.SetRailSortKey(paneRoot, index);
        return paneRoot;
    }

    private static Control _NewRealTranscript(int index)
    {
        var session = new SessionViewModel();
        session.Transcript.Clear();
        for (var i = 0; i < 40; i++)
        {
            session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, _Row(index, i)));
        }

        var next = session.Transcript.Count;
        var streaming = new DispatcherTimer(DispatcherPriority.Default) { Interval = TimeSpan.FromMilliseconds(33) };
        streaming.Tick += (_, _) => session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, _Row(index, next++)));
        streaming.Start();

        var view = new SessionView { DataContext = session };
        view.DetachedFromVisualTree += (_, _) => streaming.Stop();
        return view;
    }

    /// <summary>Rows of wildly different heights — the fault scales with the spread, not with the count.</summary>
    private static string _Row(int pane, int row) =>
        row % 4 == 0
            ? string.Join(' ', Enumerable.Repeat($"pane {pane} row {row} long", 40))
            : $"pane {pane} row {row}";

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
public sealed record SweepOptions(int MinSessions, int MaxSessions, double Width, double Height, int SettleMs, int Repeats);
