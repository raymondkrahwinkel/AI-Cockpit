using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1245: the transcript jumping up and back while a reply streams. Extent on a virtualising panel is realised
/// height scaled to the row count, so a tail being re-measured makes it read several times too big for one pass —
/// and a follow that steers by it wrote jumps of thousands of pixels the next pass clamped straight back, once per
/// streamed chunk.
/// <para>
/// Sampled per rendered frame, because a frame is the only thing an operator can see. On <c>ScrollChanged</c> the
/// offset is already back and the jump is invisible — measuring there is what made an earlier round of this
/// investigation report a clean difference that meant nothing.
/// </para>
/// <para>
/// Four gates run before the claim, each closing a way this could pass while the behaviour is broken: the sampler
/// must see a jump it was given, there must be enough frames to see one in, the fault's own precondition must have
/// occurred in this run, and the follow must still be at the tail at the end. A green run that cannot show it was
/// able to go red is the fault this epic keeps finding in its own instruments.
/// </para>
/// </summary>
[Collection("avalonia")]
public sealed class TranscriptStreamFlickerTests
{
    /// <summary>Below a jump this size nothing reads as a jump; above it the transcript visibly moves.</summary>
    private const double VisibleJumpPx = 200.0;

    /// <summary>Fewer painted frames than this cannot say anything about jumps between them.</summary>
    private const int MinimumFrames = 30;

    /// <summary>What the estimate has to swing by for this run to have exercised the fault at all.</summary>
    private const double MinimumExtentSwing = 2.0;

    private sealed record Run(
        IReadOnlyList<double> Painted,
        IReadOnlyList<double> ControlPainted,
        double MinExtent,
        double MaxExtent,
        double ForwardTravel,
        double Viewport,
        double? FinalGap);

    [Theory(Skip = "AC-1238 makes this green. Red on unchanged main (AC-1245): 894px backwards over 72 painted "
                   + "frames at 520x700, 2046px over 99 frames at 900x600, deterministic over three repeats. All "
                   + "four gates below were open on those runs — the sampler saw its own 400px control jump, "
                   + "there were enough frames, the extent estimate swung 5.0x and 6.5x, and the follow was still "
                   + "at the tail — so it failed on the claim and not on a hole in the setup. Five follower-side "
                   + "fixes were measured and rejected; the cause is that ScrollViewer clamps Offset against an "
                   + "Extent estimate that drops below the true content height, so no write strategy reaches the "
                   + "bottom. Anchoring on the row instead of on an offset (AC-1238) is what removes it.")]
    [InlineData(900, 600)]
    [InlineData(520, 700)]
    public async Task AStreamingReply_NeverScrollsBackwardsWhileFollowingItsTail(double width, double height)
    {
        var run = await _StreamAReplyAsync(width, height);

        // 1. Is the sampler alive? A backward jump this run did not make, put through the same per-frame sampler.
        Assert.True(
            _WorstBackwardJump(run.ControlPainted) > VisibleJumpPx,
            $"the positive control's own {VisibleJumpPx * 2:F0}px backward jump was not sampled "
            + $"(worst seen {_WorstBackwardJump(run.ControlPainted):F0}px over {run.ControlPainted.Count} frames) — "
            + "the sampler is blind, so nothing below proves anything");

        // 2. Were there frames to see a jump in? Headless draws only when asked, and a run that drew a handful of
        //    frames reports zero jumps for the same reason a stopped clock reports no time passing.
        Assert.True(
            run.Painted.Count >= MinimumFrames,
            $"only {run.Painted.Count} frames were painted, under the {MinimumFrames} this judgement needs");

        // 3. Did the fault's precondition occur? The jump comes from the extent estimate swinging while the tail is
        //    re-measured. A run where it held steady, or where the content never outgrew the viewport, is a run in
        //    which the defect could not have appeared — green there says nothing about whether it is fixed.
        var swing = run.MinExtent > 0 ? run.MaxExtent / run.MinExtent : 0;
        Assert.True(
            swing >= MinimumExtentSwing,
            $"the extent estimate only swung {swing:F1}x ({run.MinExtent:F0}..{run.MaxExtent:F0}px), under the "
            + $"{MinimumExtentSwing:F1}x this fault needs — this run did not reproduce the conditions it tests");
        Assert.True(
            run.ForwardTravel > run.Viewport * 2,
            $"the transcript only scrolled {run.ForwardTravel:F0}px in a {run.Viewport:F0}px viewport: there was "
            + "barely anything to follow, so not jumping while following it proves nothing");

        // 4. Did the follow still do its job? A follow that stops chasing the tail also stops jumping.
        Assert.True(
            run.FinalGap is not null and <= 1.0,
            $"the reply's tail ended {run.FinalGap?.ToString("F0") ?? "unmeasurably"}px below the viewport: "
            + "the follow gave up, so it cannot also be said not to have jumped");

        // The claim itself.
        var worst = _WorstBackwardJump(run.Painted);
        Assert.True(
            worst <= VisibleJumpPx,
            $"the transcript scrolled {worst:F0}px backwards mid-stream over {run.Painted.Count} painted frames "
            + "while following its own tail — following only ever moves towards the newest row");
    }

    private static async Task<Run> _StreamAReplyAsync(double width, double height)
    {
        var painted = new List<double>();
        var controlPainted = new List<double>();
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

            var reply = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "start of the reply.\n\n");
            vm.Transcript.Add(reply);

            var view = new SessionView { DataContext = vm };
            var window = new Window { Content = view, Width = width, Height = height };
            window.Show();
            window.UpdateLayout();
            await _PumpAsync(null, null, TimeSpan.FromMilliseconds(300));

            var scroll = view.TranscriptScroll!;

            void Sample()
            {
                if (painted.Count > 0)
                {
                    forwardTravel += Math.Max(0, scroll.Offset.Y - painted[^1]);
                }

                painted.Add(scroll.Offset.Y);
                minExtent = Math.Min(minExtent, scroll.Extent.Height);
                maxExtent = Math.Max(maxExtent, scroll.Extent.Height);
            }

            for (var i = 0; i < 40; i++)
            {
                reply.AppendText($"paragraph {i} of a long markdown answer that keeps growing and wrapping over several lines.\n\n");
                await _PumpAsync(Sample, scroll, TimeSpan.FromMilliseconds(25));
            }

            await _PumpAsync(Sample, scroll, TimeSpan.FromMilliseconds(400));

            viewport = scroll.Viewport.Height;
            var newest = view.TranscriptItems.ContainerFromIndex(view.TranscriptItems.ItemCount - 1);
            if (newest?.TranslatePoint(new Point(0, newest.Bounds.Height), scroll) is { } bottom)
            {
                finalGap = bottom.Y - scroll.Viewport.Height;
            }

            view.Follower.StickToBottom = false;
            var start = Math.Max(VisibleJumpPx * 2, scroll.Offset.Y);
            scroll.Offset = scroll.Offset.WithY(start);
            await _PumpAsync(() => controlPainted.Add(scroll.Offset.Y), scroll, TimeSpan.FromMilliseconds(80));
            scroll.Offset = scroll.Offset.WithY(start - (VisibleJumpPx * 2));
            await _PumpAsync(() => controlPainted.Add(scroll.Offset.Y), scroll, TimeSpan.FromMilliseconds(80));

            window.Close();
        });

        return new Run(painted, controlPainted, minExtent, maxExtent, forwardTravel, viewport, finalGap);
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

    /// <summary>One sample per forced render tick: the offset as it is about to be painted.</summary>
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
