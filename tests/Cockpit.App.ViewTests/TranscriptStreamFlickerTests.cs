using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1245: the transcript moving about while a reply streams. AC-1238 measured that this is three faults at once
/// and the operator confirmed he sees them, so each is asserted separately from one run.
/// <list type="number">
/// <item>The scrollbar, as where its thumb sits: <c>Offset</c> over <c>Extent - Viewport</c>, which is Avalonia's
/// own <c>ScrollBarMaximum</c>. Not <c>Offset</c> alone — when the panel's extent estimate shrinks along with it
/// the thumb does not move at all, measured over two consecutive passes as 731px of offset with the thumb pinned
/// at 1.000 both times.</item>
/// <item>The text: how far the newest row's bottom sinks below the bottom of the viewport while the follow catches
/// up, which is the transcript visibly sliding away under the reader.</item>
/// <item>The answer vanishing: frames with no end of the reply on screen at all. Not frames merely missing the
/// newest row — that one is always a frame below the fold before the follow reaches it, so a claim on the raw
/// count would be measuring physics rather than a defect. It stays as a reading beside the claim.</item>
/// </list>
/// <para>
/// They pull in different directions and a fix that buys one with another is not a fix: <c>CacheLength="1"</c> on
/// the panel took the worst offset jump to 0 in a session pane while taking the text's worst slide from 393px to
/// 1394px in the chat. So the claims are judged from one run, together, and every run reports every number.
/// </para>
/// <para>
/// Sampled per rendered frame, because a frame is the only thing an operator can see. On <c>ScrollChanged</c> the
/// offset is already back and the jump is invisible — measuring there is what made an earlier round of this
/// investigation report a clean difference that meant nothing. Streamed through the session's own event path for
/// the same reason: appending to a row the test made itself is a seam the app never uses.
/// </para>
/// <para>
/// Gates run before the claims, each closing a way this could pass while the behaviour is broken: both samplers
/// must see a movement they were given, there must be enough frames to see one in, the run must have exercised the
/// panel at all, and the follow must still be at the tail at the end. A green run that cannot show it was able to
/// go red is the fault this epic keeps finding in its own instruments.
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
        IReadOnlyList<double> Thumbs,
        IReadOnlyList<double> ControlPainted,
        IReadOnlyList<double> ControlThumbs,
        IReadOnlyList<double?> ControlGaps,
        double MinExtent,
        double MaxExtent,
        double ForwardTravel,
        double Viewport,
        double? FinalGap);

    [Theory]
    [InlineData(900, 600)]
    [InlineData(520, 700)]
    public async Task AStreamingReply_MovesNeitherItsScrollbarNorItsTextBackwards(double width, double height)
    {
        var run = await _StreamAReplyAsync(width, height);

        // 1. Are the samplers alive? Movements this run did not make, put through the same per-frame samplers.
        //    The thumb's control runs the whole track: a fixed nudge is a sliver of it once the reply is long.
        var controlThumb = _WorstBackwardJump(run.ControlThumbs) * run.Viewport;
        Assert.True(
            controlThumb > VisibleJumpPx,
            $"running the viewport the whole way to the top moved the sampled thumb {controlThumb:F0}px of a "
            + $"{run.Viewport:F0}px track over {run.ControlThumbs.Count} frames: the thumb sampler is blind, so "
            + "nothing below proves anything");
        // Either reading answers for the text sampler: a tall newest row still measures, a small one leaves the
        // viewport and stops being measurable at all.
        Assert.True(
            run.ControlGaps.Any(gap => gap is null || gap > VisibleJumpPx),
            $"the same control move slid the newest row {_WorstSlide(run.ControlGaps):F0}px off the bottom, never "
            + "past the viewport, and the gap sampler reported neither: the text sampler is blind, so its claim "
            + "below proves nothing");

        // 2. Were there frames to see movement in? Headless draws only when asked, and a handful of frames reports
        //    zero movement for the same reason a stopped clock reports no time passing.
        Assert.True(
            run.Painted.Count >= MinimumFrames,
            $"only {run.Painted.Count} frames were painted, under the {MinimumFrames} this judgement needs");

        // 3. Did this run exercise the machinery at all? It looks mild because AC-1238 measured that the extent
        //    swing it used to demand was the follower's own doing, and demanding the swing back is demanding the
        //    bug back.
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

        // Judged together, so a variant trading one fault for another cannot report only the half that got worse.
        // The thumb is Offset over Extent - Viewport (Avalonia's ScrollBarMaximum), not Offset: when the extent
        // estimate shrinks along with it, the thumb does not move at all.
        var worstThumb = _WorstBackwardJump(run.Thumbs) * run.Viewport;
        var worstJump = _WorstBackwardJump(run.Painted);
        var worstSlide = _WorstSlide(run.Gaps);
        var faults = new List<string>();

        // Claim 1 — the scrollbar, as the operator sees it: where the thumb sits on its track.
        if (worstThumb > VisibleJumpPx)
        {
            faults.Add(
                $"the scrollbar thumb ran {worstThumb:F0}px backwards along a {run.Viewport:F0}px track mid-stream "
                + "(main: 335-403px, 72-76% of the whole track, three or four times per reply) — the thumb only "
                + "ever moves towards the newest row");
        }

        // Claim 2 — the text. Only half of the fault: with the newest row unrealised its slide is unmeasurable
        // rather than zero, which is what claim 3 counts.
        if (worstSlide > VisibleJumpPx)
        {
            faults.Add(
                $"the newest row sank {worstSlide:F0}px below the bottom of a {run.Viewport:F0}px viewport (main: "
                + "352-441px at 900x600, 440-557px at 520x700 over three repeats) — the text the operator is "
                + "reading visibly slid away while the reply was still arriving");
        }

        // Claim 3 — the answer vanishing. On frames with no end of the reply on screen, not on frames missing only
        // the newest row: that one is always a frame below the fold before the follow reaches it, so a claim on the
        // raw count would measure physics rather than a defect. It stays as a reading beside this one.
        var blindFrames = run.Gaps.Count(gap => gap is null);
        var replyNowhere = run.TailNowhere.Count(x => x);
        if (replyNowhere > 0)
        {
            faults.Add(
                $"{replyNowhere} of {run.Painted.Count} painted frames had no end of the reply on screen at all "
                + "(main: 11-16 over six repeats) — the panel was drawing another part of the transcript while "
                + "the follow was on the reply, which is the answer vanishing and returning rather than sliding");
        }

        Assert.True(
            faults.Count == 0,
            string.Join("; and ", faults)
            + $" [all readings this run: thumb {worstThumb:F0}px of a {run.Viewport:F0}px track, offset "
            + $"{worstJump:F0}px, text {worstSlide:F0}px, "
            + $"{blindFrames} frames without the newest row, of which {replyNowhere} had no end of the reply on "
            + $"screen at all, over {run.Painted.Count} painted frames]");
    }

    private static async Task<Run> _StreamAReplyAsync(double width, double height)
    {
        var painted = new List<double>();
        var gaps = new List<double?>();
        var tailNowhere = new List<bool>();
        var thumbs = new List<double>();
        var controlPainted = new List<double>();
        var controlThumbs = new List<double>();
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

            // Where the thumb sits on its track: Avalonia's ScrollBarMaximum is Extent - Viewport.
            double Thumb() =>
                scroll.Offset.Y / Math.Max(1.0, scroll.Extent.Height - scroll.Viewport.Height);

            // How far the newest row's bottom sits below the viewport, which is the text sliding away. Null while
            // the panel has not realised the row: below by an amount nothing can measure, and a guess averaged
            // into the claim would be worse than saying so.
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
                thumbs.Add(Thumb());
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
                controlThumbs.Add(Thumb());
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

            // One positive control per sampler, the follow switched off so nothing corrects them: the whole track
            // for the thumb, and for the gap a reply that grows while nothing follows it.
            view.Follower.StickToBottom = false;
            var start = scroll.Offset.Y;
            await _PumpAsync(ControlSample, scroll, TimeSpan.FromMilliseconds(80));
            scroll.Offset = scroll.Offset.WithY(0);
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
            thumbs,
            controlPainted,
            controlThumbs,
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
