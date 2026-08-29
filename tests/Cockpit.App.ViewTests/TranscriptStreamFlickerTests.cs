using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1245: the transcript moving about while a reply streams. AC-1238 measured that this is two faults at once, and
/// the operator confirmed he sees both, so both are asserted here — separately, from one run.
/// <list type="number">
/// <item>The scrollbar. <c>Offset</c> is renumbered whenever the virtualising panel's realised window changes its
/// first index, because the panel's origin is that index times an average row height the growing tail dominates.
/// The thumb jumps hundreds of pixels while the drawn text provably does not move (measured: offset 4875 to 3787
/// with the tail's screen position 241 before and 241 after).</item>
/// <item>The text. The newest row's bottom slides away from the bottom of the viewport while the follow catches up,
/// which is the transcript visibly sinking.</item>
/// </list>
/// <para>
/// They pull in opposite directions and a fix that buys one with the other is not a fix: <c>CacheLength="1"</c> on
/// the panel took the worst offset jump to 0 in a session pane while taking the text's worst slide from 393px to
/// 1394px in the chat (AC-1238, per layout pass). So both claims are judged from one run, together, and neither is
/// allowed to regress.
/// </para>
/// <para>
/// Sampled per rendered frame, because a frame is the only thing an operator can see. On <c>ScrollChanged</c> the
/// offset is already back and the jump is invisible — measuring there is what made an earlier round of this
/// investigation report a clean difference that meant nothing.
/// </para>
/// <para>
/// Gates run before the claims, each closing a way this could pass while the behaviour is broken: both samplers must
/// see a movement they were given, there must be enough frames to see one in, the run must have exercised the panel
/// at all, and the follow must still be at the tail at the end. A green run that cannot show it was able to go red
/// is the fault this epic keeps finding in its own instruments.
/// </para>
/// </summary>
[Collection("avalonia")]
public sealed class TranscriptStreamFlickerTests
{
    /// <summary>Below a movement this size nothing reads as a jump; above it the transcript visibly moves.</summary>
    private const double VisibleJumpPx = 200.0;

    /// <summary>Fewer painted frames than this cannot say anything about movement between them.</summary>
    private const int MinimumFrames = 30;

    private sealed record Run(
        IReadOnlyList<double> Painted,
        IReadOnlyList<double?> Gaps,
        IReadOnlyList<bool> TailNowhere,
        IReadOnlyList<double> ControlPainted,
        IReadOnlyList<double?> ControlGaps,
        double MinExtent,
        double MaxExtent,
        double ForwardTravel,
        double Viewport,
        double? FinalGap);

    [Theory(Skip = "AC-1238 has the text and the vanishing answer; the scrollbar claim is what is left. Red on "
                   + "unchanged main with every gate open, three repeats, scrollbar / text / frames without the "
                   + "newest row of which with no end of the reply on screen at all: 900x600 gives 924-1260px, "
                   + "286-489px and 14-16 of which 14-15; 520x700 gives 699-1333px, 197-557px and 15-16 of which "
                   + "14. With AC-1238's row-per-block split: 218px and 418px, 0px, and 40 of which 0 — the 40 "
                   + "are the newest block sitting a frame below the fold while the block before it is still on "
                   + "screen, which is why that fourth reading is what tells the two apart. Do not trade one "
                   + "claim for the other: CacheLength=1 takes the scrollbar to 0 and the text to 1394px.")]
    [InlineData(900, 600)]
    [InlineData(520, 700)]
    public async Task AStreamingReply_MovesNeitherItsScrollbarNorItsTextBackwards(double width, double height)
    {
        var run = await _StreamAReplyAsync(width, height);

        // 1. Are the samplers alive? A backward jump this run did not make, put through the same per-frame sampler,
        //    which moves the thumb and the text at once — so one control answers for both claims.
        Assert.True(
            _WorstBackwardJump(run.ControlPainted) > VisibleJumpPx,
            $"the positive control's own {VisibleJumpPx * 2:F0}px backward jump was not sampled "
            + $"(worst seen {_WorstBackwardJump(run.ControlPainted):F0}px over {run.ControlPainted.Count} frames) — "
            + "the offset sampler is blind, so nothing below proves anything");
        // The text sampler has two readings and the control lands on whichever applies: a newest row taller than
        // the control's 400px still measures, a small one leaves the viewport and stops being measurable at all.
        // Requiring the first alone made this gate fail the moment AC-1238 made the newest row a single block.
        Assert.True(
            run.ControlGaps.Any(gap => gap is null || gap > VisibleJumpPx),
            $"the same control move slid the newest row {_WorstSlide(run.ControlGaps):F0}px off the bottom, never "
            + "past the viewport, and the gap sampler reported neither: the text sampler is blind, so its claim "
            + "below proves nothing");

        // 2. Were there frames to see movement in? Headless draws only when asked, and a run that drew a handful of
        //    frames reports zero movement for the same reason a stopped clock reports no time passing.
        Assert.True(
            run.Painted.Count >= MinimumFrames,
            $"only {run.Painted.Count} frames were painted, under the {MinimumFrames} this judgement needs");

        // 3. Did this run exercise the machinery at all? Both faults need a panel estimating rows it has not
        //    realised, and a tail moving far enough that the follow has to keep correcting.
        //    This gate looks mild for a reason. AC-1238 measured why it is no longer phrased as "the extent estimate
        //    swung >=2x": that swing was the follower's own doing. Its jump to the estimated end is what made the
        //    panel re-estimate from 1827 to 9139px; with the jump gone the same stream stays inside 1827..2976, and
        //    demanding the swing back is demanding the bug back.
        Assert.True(
            run.MaxExtent > run.Viewport * 3,
            $"the transcript never grew past {run.MaxExtent:F0}px in a {run.Viewport:F0}px viewport: too little of "
            + "it was ever unrealised for the panel to be estimating anything");
        Assert.True(
            run.ForwardTravel > run.Viewport * 2,
            $"the transcript only scrolled {run.ForwardTravel:F0}px in a {run.Viewport:F0}px viewport: there was "
            + "barely anything to follow, so not moving while following it proves nothing");

        // 4. Did the follow still do its job? A follow that stops chasing the tail also stops moving.
        Assert.True(
            run.FinalGap is not null and <= 1.0,
            $"the reply's tail ended {run.FinalGap?.ToString("F0") ?? "unmeasurably"}px below the viewport: "
            + "the follow gave up, so it cannot also be said not to have moved");

        // The claims, judged together so that every run reports every number. Asserting them one after the
        // other would stop at the first, and a variant that trades one fault for the other would report only the
        // half that got worse — which is exactly the trade this test exists to catch.
        var worstJump = _WorstBackwardJump(run.Painted);
        var worstSlide = _WorstSlide(run.Gaps);
        var faults = new List<string>();

        // Claim 1 — the scrollbar.
        if (worstJump > VisibleJumpPx)
        {
            faults.Add(
                $"the scrollbar ran {worstJump:F0}px backwards mid-stream (main: 804-1260px at 900x600, "
                + "894-1102px at 520x700 over three repeats) — the thumb only ever moves towards the newest row, "
                + "whether or not the text under it moves with it");
        }

        // Claim 2 — the text, in two readings, because one of them cannot see the worst of it. A frame in which the
        // panel has not realised the newest row at all is a frame drawn from somewhere else in the transcript
        // entirely, and its slide is unmeasurable rather than zero: main alternates between the tail alone
        // (first=12, gap 0) and rows 3..8 with the extent at 6100px, which is the operator watching his answer
        // disappear and come back. Counting those frames is what keeps the measurable half from flattering it.
        if (worstSlide > VisibleJumpPx)
        {
            faults.Add(
                $"the newest row sank {worstSlide:F0}px below the bottom of a {run.Viewport:F0}px viewport (main: "
                + "352-441px at 900x600, 440-557px at 520x700 over three repeats) — the text the operator is "
                + "reading visibly slid away while the reply was still arriving");
        }

        var blindFrames = run.Gaps.Count(gap => gap is null);
        if (blindFrames > 0)
        {
            faults.Add(
                $"{blindFrames} of {run.Gaps.Count} painted frames did not contain the newest row at all (main: "
                + "14-16 of about 90) — the "
                + "panel was drawing another part of the transcript while the follow was on it, which is the "
                + "answer vanishing and returning rather than sliding");
        }

        Assert.True(
            faults.Count == 0,
            string.Join("; and ", faults)
            + $" [all readings this run: scrollbar {worstJump:F0}px, text {worstSlide:F0}px, "
            + $"{run.Gaps.Count(gap => gap is null)} frames without the newest row of which "
            + $"{run.TailNowhere.Count(x => x)} had no end of the reply on screen at all, over "
            + $"{run.Painted.Count} painted frames]");
    }

    private static async Task<Run> _StreamAReplyAsync(double width, double height)
    {
        var painted = new List<double>();
        var gaps = new List<double?>();
        var tailNowhere = new List<bool>();
        var controlPainted = new List<double>();
        var controlGaps = new List<double?>();
        var minExtent = double.MaxValue;
        var maxExtent = 0.0;
        var forwardTravel = 0.0;
        var viewport = 0.0;
        double? finalGap = null;

        await HeadlessAvalonia.RunAsync(async () =>
        {
            var vm = new SessionViewModel();
            vm.Transcript.Clear();
            for (var i = 0; i < 12; i++)
            {
                vm.Transcript.Add(new TranscriptEntryViewModel(
                    TranscriptEntryKind.AssistantText,
                    string.Join(' ', Enumerable.Repeat($"filler row {i} with enough words to wrap a few times", 12))));
            }

            // Streamed through the session's own event path, not by appending to a row this test made itself:
            // AC-1238 puts the row-per-block split there, and a test that calls AppendText behind it measures a
            // seam the app never uses.
            vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "start of the reply.\n\n" });

            var view = new SessionView { DataContext = vm };
            var window = new Window { Content = view, Width = width, Height = height };
            window.Show();
            window.UpdateLayout();
            await _PumpAsync(null, null, TimeSpan.FromMilliseconds(300));

            var scroll = view.TranscriptScroll!;

            // How far the newest row's bottom sits below the bottom of the viewport, which is the text sliding away.
            // Null while the panel has not realised the row: it is somewhere below by an amount nothing can measure,
            // and averaging a guess into the claim would be worse than saying so.
            double? TailGap()
            {
                var newest = view.TranscriptItems.ContainerFromIndex(view.TranscriptItems.ItemCount - 1);
                return newest?.TranslatePoint(new Point(0, newest.Bounds.Height), scroll) is { } bottom
                    ? bottom.Y - scroll.Viewport.Height
                    : null;
            }

            void Sample()
            {
                if (painted.Count > 0)
                {
                    forwardTravel += Math.Max(0, scroll.Offset.Y - painted[^1]);
                }

                painted.Add(scroll.Offset.Y);
                gaps.Add(TailGap());
                var last = view.TranscriptItems.ItemCount - 1;
                tailNowhere.Add(
                    view.TranscriptItems.ContainerFromIndex(last) is null
                    && (last < 1 || view.TranscriptItems.ContainerFromIndex(last - 1) is null));
                minExtent = Math.Min(minExtent, scroll.Extent.Height);
                maxExtent = Math.Max(maxExtent, scroll.Extent.Height);
            }

            void ControlSample()
            {
                controlPainted.Add(scroll.Offset.Y);
                controlGaps.Add(TailGap());
            }

            for (var i = 0; i < 40; i++)
            {
                vm.Apply(new AssistantTextDelta
                {
                    SessionId = "S1",
                    BlockIndex = 0,
                    Text = $"paragraph {i} of a long markdown answer that keeps growing and wrapping over several lines.\n\n",
                });
                await _PumpAsync(Sample, scroll, TimeSpan.FromMilliseconds(25));
            }

            await _PumpAsync(Sample, scroll, TimeSpan.FromMilliseconds(400));

            viewport = scroll.Viewport.Height;
            finalGap = TailGap();

            // Two positive controls, one per sampler, both with the follow switched off so nothing corrects them.
            // The offset sampler gets a backward jump of its own; the gap sampler gets a reply that grows while
            // nothing follows it, which is the exact movement its claim is about. Moving the viewport was the
            // control for both until AC-1238: with the newest row a single block, the same 400px left it neither
            // measurably lower nor off the viewport, so the gate fired on three runs in six for no fault.
            view.Follower.StickToBottom = false;
            var start = Math.Max(VisibleJumpPx * 2, scroll.Offset.Y);
            scroll.Offset = scroll.Offset.WithY(start);
            await _PumpAsync(ControlSample, scroll, TimeSpan.FromMilliseconds(80));
            scroll.Offset = scroll.Offset.WithY(start - (VisibleJumpPx * 2));
            await _PumpAsync(ControlSample, scroll, TimeSpan.FromMilliseconds(80));

            scroll.Offset = scroll.Offset.WithY(start);
            await _PumpAsync(ControlSample, scroll, TimeSpan.FromMilliseconds(40));
            for (var i = 0; i < 12; i++)
            {
                vm.Apply(new AssistantTextDelta
                {
                    SessionId = "S1",
                    BlockIndex = 0,
                    Text = $"unfollowed paragraph {i} that the viewport is deliberately not chasing.\n\n",
                });
            }

            await _PumpAsync(ControlSample, scroll, TimeSpan.FromMilliseconds(120));

            window.Close();
        });

        return new Run(
            painted,
            gaps,
            tailNowhere,
            controlPainted,
            controlGaps,
            minExtent,
            maxExtent,
            forwardTravel,
            viewport,
            finalGap);
    }

    private static double _WorstBackwardJump(IReadOnlyList<double> offsets)
    {
        var worst = 0.0;
        for (var i = 1; i < offsets.Count; i++)
        {
            worst = Math.Max(worst, offsets[i - 1] - offsets[i]);
        }

        return worst;
    }

    /// <summary>The furthest the newest row's bottom ever sat below the viewport over these frames.</summary>
    private static double _WorstSlide(IReadOnlyList<double?> gaps)
    {
        var worst = 0.0;
        foreach (var gap in gaps)
        {
            if (gap is { } value)
            {
                worst = Math.Max(worst, value);
            }
        }

        return worst;
    }

    /// <summary>One sample per forced render tick: the transcript as it is about to be painted.</summary>
    private static async Task _PumpAsync(Action? sample, ScrollViewer? scroll, TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < until)
        {
            if (sample is not null && scroll is not null)
            {
                sample();
            }

            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(8);
        }
    }
}
