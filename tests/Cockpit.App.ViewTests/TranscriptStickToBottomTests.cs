using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-459: the "Thinking…" row, the starting banner, the usage-warning bar and the pending-resume bar all dock
/// above the composer, so each one appearing or disappearing resizes the transcript's viewport without adding or
/// removing any transcript content. <c>_OnTranscriptScrollChanged</c> used to read that viewport-driven offset
/// shift as a real user scroll (offset moves, extent does not) and re-derive whether the transcript was still
/// following the tail — so parked at the newest row, any of those rows appearing could silently stop the follow
/// and leave the jump-to-newest button showing with nothing the operator did to explain it.
/// </summary>
[Collection("avalonia")]
public class TranscriptStickToBottomTests
{
    private static ScrollViewer _Transcript(Visual root) =>
        root.GetVisualDescendants().OfType<ScrollViewer>().First(scroll => scroll.Name == "TranscriptScroll");

    private static Button _JumpToNewestButton(Visual root) =>
        root.GetVisualDescendants().OfType<Button>().First(button => button.Name == "ScrollToBottomButton");

    private static void _Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static Point _CenterOf(Visual control, Visual root) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;

    /// <summary>
    /// A real mouse wheel tick (not a raw Offset assignment) so the transcript's input latch (AC-528) actually
    /// sees it — the SUT now derives "was this the operator" from input events, not from scroll deltas, so a test
    /// that wants to simulate a user scroll has to go through real input or it measures nothing.
    /// </summary>
    private static void _WheelTicks(Window window, ScrollViewer scroll, double deltaY, int count)
    {
        var point = _CenterOf(scroll, window);
        for (var i = 0; i < count; i++)
        {
            window.MouseWheel(point, new Vector(0, deltaY), RawInputModifiers.None);
            _Settle(window);
        }
    }

    /// <summary>A real scrollbar-thumb drag: press, drag far past the end (clamped by the scrollbar itself), release.</summary>
    private static void _DragThumbToEnd(Window window, ScrollViewer scroll)
    {
        var thumb = scroll.GetVisualDescendants().OfType<Thumb>().First();
        var start = _CenterOf(thumb, window);
        window.MouseDown(start, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        _Settle(window);
        window.MouseMove(new Point(start.X, start.Y + 4000), RawInputModifiers.LeftMouseButton);
        _Settle(window);
        window.MouseUp(new Point(start.X, start.Y + 4000), MouseButton.Left, RawInputModifiers.None);
        _Settle(window);
    }

    /// <summary>A session with enough rows to overflow the window, parked at the newest one.</summary>
    private static (Window Window, SessionViewModel Session, ScrollViewer Scroll, Button JumpButton) _PaneParkedAtTheBottom()
    {
        var session = new SessionViewModel();
        for (var index = 0; index < 60; index++)
        {
            session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, $"row {index}"));
        }

        var window = new Window { Width = 620, Height = 480, Content = new SessionView { DataContext = session } };
        window.Show();
        _Settle(window);

        var scroll = _Transcript(window);
        var jumpButton = _JumpToNewestButton(window);

        scroll.ScrollToEnd();
        _Settle(window);
        Assert.False(jumpButton.IsVisible, "the fixture must start parked at the newest row");

        return (window, session, scroll, jumpButton);
    }

    /// <summary>
    /// Toggles a viewport-resizing row on and off, repeatedly — like a turn with several tool calls, which flips
    /// <c>IsAwaitingResponse</c> many times over (AC-459) — and asserts the follow never breaks: the jump-to-newest
    /// button stays hidden throughout, and the transcript stays measured at the bottom by the same geometry the
    /// handler itself uses (<see cref="TranscriptScrollAnchor"/>).
    /// <para>
    /// A single toggle is not enough to catch a regression here: Avalonia's own virtualising panel re-anchors the
    /// offset to the new bottom the first time the viewport resizes, whether or not the fix is in place. It is only
    /// from the second cycle onward that the two diverge — without the fix, a shrink that lands (correctly) on the
    /// true bottom is never revisited on the next shrink, because nothing else moved the offset in between, so the
    /// transcript stays measurably short of the bottom for as long as the row is up.
    /// </para>
    /// </summary>
    private static void _AssertTogglingNeverStopsFollowing(string label, Action<SessionViewModel, bool> toggle)
    {
        var (window, session, scroll, jumpButton) = _PaneParkedAtTheBottom();
        var restingViewport = scroll.Viewport.Height;

        for (var cycle = 0; cycle < 3; cycle++)
        {
            toggle(session, true);
            _Settle(window);

            Assert.NotEqual(restingViewport, scroll.Viewport.Height);
            Assert.False(jumpButton.IsVisible, $"{label} appearing (cycle {cycle}) is not a user scroll, so following must not stop");
            Assert.True(
                TranscriptScrollAnchor.IsAtBottom(scroll.Offset.Y, scroll.Extent.Height, scroll.Viewport.Height),
                $"{label} must not leave the transcript short of the bottom it was parked at (cycle {cycle})");

            toggle(session, false);
            _Settle(window);

            Assert.False(jumpButton.IsVisible, $"{label} disappearing again must not stop the follow either (cycle {cycle})");
            Assert.True(
                TranscriptScrollAnchor.IsAtBottom(scroll.Offset.Y, scroll.Extent.Height, scroll.Viewport.Height),
                $"{label} disappearing must leave the transcript at the bottom too (cycle {cycle})");
        }

        window.Close();
    }

    [Fact]
    public void TheThinkingIndicator_AppearingWhileParkedAtTheBottom_DoesNotStopFollowing() => HeadlessAvalonia.Run(() =>
        _AssertTogglingNeverStopsFollowing("the Thinking indicator", (session, on) => session.IsAwaitingResponse = on));

    [Fact]
    public void TheStartingBanner_AppearingWhileParkedAtTheBottom_DoesNotStopFollowing() => HeadlessAvalonia.Run(() =>
        _AssertTogglingNeverStopsFollowing("the starting banner", (session, on) => session.IsStarting = on));

    [Fact]
    public void TheUsageWarningBar_AppearingWhileParkedAtTheBottom_DoesNotStopFollowing() => HeadlessAvalonia.Run(() =>
        _AssertTogglingNeverStopsFollowing(
            "the usage-warning bar", (session, on) => session.UsageWarning = on ? "Context filling up." : string.Empty));

    [Fact]
    public void ThePendingResumeBar_AppearingWhileParkedAtTheBottom_DoesNotStopFollowing() => HeadlessAvalonia.Run(() =>
        _AssertTogglingNeverStopsFollowing(
            "the pending-resume bar", (session, on) => session.PendingResumeLabel = on ? "Resuming Mon 07:30" : string.Empty));

    [Fact]
    public void ARealUserScroll_StillStopsAndResumesFollowing() => HeadlessAvalonia.Run(() =>
    {
        var (window, _, scroll, jumpButton) = _PaneParkedAtTheBottom();

        // A real user scroll: the offset moves on its own, with neither the extent nor the viewport changing —
        // exactly the case this handler exists for (AC3). A modest scroll rather than a jump to the top, so the
        // virtualising panel does not derealise most of the transcript and shift the (estimated) extent along
        // with it — that would be a second, unrelated way for the offset to move.
        scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Offset.Y - 150));
        _Settle(window);
        Assert.True(jumpButton.IsVisible, "the operator scrolled away from the tail");

        scroll.ScrollToEnd();
        _Settle(window);
        Assert.False(jumpButton.IsVisible, "scrolling back to the bottom must resume following");

        window.Close();
    });

    /// <summary>A session whose rows swing wildly between one-liners and long multi-paragraph replies, the kind of
    /// mix a real transcript has (a short "OK" next to a big Bash/Read result) — the shape needed to reproduce
    /// AC-528: it makes the VirtualizingStackPanel re-estimate its own Extent on almost every scroll step.</summary>
    private static (Window Window, SessionViewModel Session, ScrollViewer Scroll, Button JumpButton) _PaneWithWildlyVaryingRowHeights()
    {
        var session = new SessionViewModel();
        var longBody = string.Join("\n\n", Enumerable.Range(0, 6).Select(i => $"Paragraph {i} — {new string('x', 80)}"));
        for (var index = 0; index < 200; index++)
        {
            var text = index % 7 == 0 ? longBody : $"row {index}";
            session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, text));
        }

        var window = new Window { Width = 700, Height = 500, Content = new SessionView { DataContext = session } };
        window.Show();
        _Settle(window);

        var scroll = _Transcript(window);
        var jumpButton = _JumpToNewestButton(window);

        // 200 rows this tall need more than one layout pass for the panel's estimated Extent to settle, so a
        // single ScrollToEnd() can land short of the true bottom the first time — repeat until the geometry itself
        // says we are there, the same thing a real (slower) user scroll would eventually see too.
        for (var attempt = 0; attempt < 10 && jumpButton.IsVisible; attempt++)
        {
            scroll.ScrollToEnd();
            _Settle(window);
        }

        Assert.False(jumpButton.IsVisible, "the fixture must start parked at the newest row");

        return (window, session, scroll, jumpButton);
    }

    /// <summary>
    /// AC-528: measured with a throwaway harness that logged every ScrollChanged event over this exact fixture —
    /// <c>ExtentDelta.Y</c> was nonzero on 40 of 60 consecutive wheel-sized steps, because the virtualising panel
    /// keeps re-estimating Extent as tall and short rows realise/derealise. The old handler read any nonzero
    /// ExtentDelta as "not a user scroll" and, since <c>_stickToBottom</c> was still true from being parked at the
    /// bottom, called <c>ScrollToEnd()</c> on that same event — so the offset it had just been set to was
    /// immediately overwritten. Reproduced directly: the same scroll-up-from-the-bottom the operator performs with
    /// the mouse wheel.
    /// </summary>
    [Fact]
    public void AWheelScrollUp_OverWildlyVaryingRowHeights_IsNotPulledBackToTheBottom() => HeadlessAvalonia.Run(() =>
    {
        var (window, _, scroll, jumpButton) = _PaneWithWildlyVaryingRowHeights();

        var offsetBeforeScroll = scroll.Offset.Y;
        _WheelTicks(window, scroll, deltaY: 1, count: 3);

        Assert.True(scroll.Offset.Y < offsetBeforeScroll, "the wheel-up must actually move the offset, not get pulled back");
        Assert.True(jumpButton.IsVisible, "the operator scrolled away from the tail, so the jump button must show");

        window.Close();
    });

    /// <summary>
    /// The other half of AC-528: scrolling back down to the bottom over the same wildly-varying rows must resume
    /// following on its own — the operator should not have to click the jump button just because the estimated
    /// Extent moved on the way down too.
    /// </summary>
    [Fact]
    public void ScrollingBackToTheBottom_OverWildlyVaryingRowHeights_ResumesFollowingWithoutAClick() => HeadlessAvalonia.Run(() =>
    {
        var (window, _, scroll, jumpButton) = _PaneWithWildlyVaryingRowHeights();

        _WheelTicks(window, scroll, deltaY: 1, count: 40);
        Assert.True(jumpButton.IsVisible, "the operator scrolled well away from the tail");

        // Walk back down in the same small wheel-sized ticks a real scroll produces, rather than one jump —
        // the bug only shows up step-by-step, because each step is where the Extent re-estimate happens.
        for (var step = 0; step < 60 && jumpButton.IsVisible; step++)
        {
            _WheelTicks(window, scroll, deltaY: -1, count: 1);
        }

        Assert.False(jumpButton.IsVisible, "scrolling back to the bottom must resume following without an explicit click");
        window.Close();
    });

    /// <summary>
    /// AC-528 (adversarial-review finding): passive streaming, no user interaction at all. Parked at the bottom, new
    /// rows of alternating short/long height arrive one at a time — a short reply next to a long tool result,
    /// exactly the mix <see cref="_PaneWithWildlyVaryingRowHeights"/> uses. The VirtualizingStackPanel nudges its
    /// own Offset as part of re-estimating Extent for the new row (measured 84-138px on a real run, not equal to
    /// ExtentDelta), which is <c>OffsetDelta.Y != 0</c> on an event nobody scrolled. The handler used to gate its
    /// "keep following" action on <c>OffsetDelta.Y == 0</c>, so it skipped re-snapping to the true bottom on that
    /// event, and the immediately following (unconditional) geometry re-derivation read the panel's partial,
    /// not-yet-caught-up offset as "not at the bottom" — permanently, since nothing else ever moved the offset
    /// again to correct it. Distinguishing this from a real user scroll: a genuine upward scroll always decreases
    /// Offset.Y (confirmed over 60 real wheel-up steps on this fixture: never once positive), so gating on
    /// <c>OffsetDelta.Y &gt;= 0</c> instead keeps following through the panel's own positive nudges while still
    /// never overriding an upward scroll.
    /// </summary>
    [Fact]
    public void StreamingRowsOfVaryingHeight_WithNoUserInteraction_NeverStopsFollowing() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        var window = new Window { Width = 700, Height = 500, Content = new SessionView { DataContext = session } };
        window.Show();
        _Settle(window);

        var scroll = _Transcript(window);
        var jumpButton = _JumpToNewestButton(window);
        var longBody = string.Join("\n\n", Enumerable.Range(0, 6).Select(i => $"Paragraph {i} — {new string('x', 80)}"));

        for (var index = 0; index < 300; index++)
        {
            var text = index % 7 == 0 ? longBody : $"row {index}";
            session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, text));
            _Settle(window);

            Assert.False(jumpButton.IsVisible, $"no user ever scrolled, so following must not stop (row {index})");
            Assert.True(
                TranscriptScrollAnchor.IsAtBottom(scroll.Offset.Y, scroll.Extent.Height, scroll.Viewport.Height),
                $"the transcript must stay measured at the true bottom while streaming passively (row {index})");
        }

        window.Close();
    });

    /// <summary>
    /// Same scenario, coarser granularity: AC-529 coalesces a turn's deltas, so production more often adds rows in
    /// batches (~40) between layout passes than one at a time. The bug reproduces at both granularities — the
    /// trigger is the panel's own Offset nudge on a resize, not the batch size.
    /// </summary>
    [Fact]
    public void StreamingRowsOfVaryingHeight_InBatchesOfFortyWithNoUserInteraction_NeverStopsFollowing() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        var window = new Window { Width = 700, Height = 500, Content = new SessionView { DataContext = session } };
        window.Show();
        _Settle(window);

        var scroll = _Transcript(window);
        var jumpButton = _JumpToNewestButton(window);
        var longBody = string.Join("\n\n", Enumerable.Range(0, 6).Select(i => $"Paragraph {i} — {new string('x', 80)}"));

        for (var batch = 0; batch < 8; batch++)
        {
            for (var i = 0; i < 40; i++)
            {
                var index = batch * 40 + i;
                var text = index % 7 == 0 ? longBody : $"row {index}";
                session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, text));
            }

            _Settle(window);

            Assert.False(jumpButton.IsVisible, $"no user ever scrolled, so following must not stop (batch {batch})");
            Assert.True(
                TranscriptScrollAnchor.IsAtBottom(scroll.Offset.Y, scroll.Extent.Height, scroll.Viewport.Height),
                $"the transcript must stay measured at the true bottom while streaming passively (batch {batch})");
        }

        window.Close();
    });

    /// <summary>
    /// The remaining cases from the adversarial review's checklist, combined: a user reading history (not at the
    /// bottom) sees a new tall row stream in and must not be dragged back down; a fast scrollbar drag straight to
    /// the bottom (one big jump, not the small wheel-sized steps used elsewhere) must resume following; and
    /// already being exactly at the bottom and scrolling further down is a no-op that must not un-follow.
    /// </summary>
    [Fact]
    public void ReadingHistoryDuringAStream_ThenAFastScrollbarDragToTheBottom_BehavesCorrectlyThroughout() => HeadlessAvalonia.Run(() =>
    {
        var (window, session, scroll, jumpButton) = _PaneWithWildlyVaryingRowHeights();
        var longBody = string.Join("\n\n", Enumerable.Range(0, 6).Select(i => $"Paragraph {i} — {new string('x', 80)}"));

        _WheelTicks(window, scroll, deltaY: 1, count: 40);
        Assert.True(jumpButton.IsVisible, "reading history: scrolled away from the tail");

        // A new (tall) row streams in while the operator is reading history — must not pull them back down.
        session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, longBody));
        _Settle(window);
        Assert.True(jumpButton.IsVisible, "a row streaming in while reading history must not resume following on its own");

        // Fast scrollbar drag: a real press-drag-release on the thumb, straight past the end, rather than the
        // small wheel-sized ticks used elsewhere — this is the _draggingThumb latch path specifically.
        _DragThumbToEnd(window, scroll);
        Assert.False(jumpButton.IsVisible, "a fast drag straight to the bottom must resume following");

        // Already at the bottom: scrolling further down is a no-op and must not un-follow.
        var offsetAtBottom = scroll.Offset.Y;
        _WheelTicks(window, scroll, deltaY: -1, count: 3);
        Assert.Equal(offsetAtBottom, scroll.Offset.Y);
        Assert.False(jumpButton.IsVisible, "scrolling further down while already at the bottom must stay following");

        window.Close();
    });

    /// <summary>
    /// Confirming-review gap: no existing test streamed through the real path — production grows one row's Text
    /// via <see cref="TranscriptEntryViewModel.AppendText"/> (delta-by-delta), never adds a new row per delta. A
    /// row growing taller in place is exactly the "Extent changes, nobody scrolled" case the latch has to cover.
    /// </summary>
    [Fact]
    public void AppendingStreamedTextToTheLastRow_WithNoUserInteraction_NeverStopsFollowing() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        session.Transcript.Clear();
        for (var index = 0; index < 30; index++)
        {
            session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, $"row {index}"));
        }

        var window = new Window { Width = 700, Height = 500, Content = new SessionView { DataContext = session } };
        window.Show();
        _Settle(window);

        var scroll = _Transcript(window);
        var jumpButton = _JumpToNewestButton(window);
        for (var attempt = 0; attempt < 10 && jumpButton.IsVisible; attempt++)
        {
            scroll.ScrollToEnd();
            _Settle(window);
        }

        Assert.False(jumpButton.IsVisible, "the fixture must start parked at the newest row");

        var reply = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, string.Empty);
        session.Transcript.Add(reply);
        _Settle(window);

        const string delta = "lorem ipsum dolor sit amet consectetur adipiscing elit ";
        for (var tick = 0; tick < 200; tick++)
        {
            reply.AppendText(delta);
            _Settle(window);

            Assert.False(jumpButton.IsVisible, $"no user ever scrolled, so following must not stop (tick {tick})");
            Assert.True(
                TranscriptScrollAnchor.IsAtBottom(scroll.Offset.Y, scroll.Extent.Height, scroll.Viewport.Height),
                $"the transcript must stay measured at the true bottom while text streams in (tick {tick})");
        }

        window.Close();
    });

    /// <summary>
    /// Confirming-review finding: at Focus, a run's second consecutive auto tool call folds the run under one
    /// anchor via <c>SessionViewModel._RecomputeReadingGroups()</c> — the first row (until now a lone, fully-shown
    /// tool call) shrinks to just its "N steps run" line, and the row just added collapses to nothing
    /// (<see cref="TranscriptEntryViewModel.IsGroupMember"/>, not yet expanded), both in the same collection-changed
    /// pass the second row's own Add triggers. A row above changes height in the exact pass a row arrives below it
    /// — an ordinary Focus-level session, no click involved.
    /// </summary>
    [Fact]
    public void AFoldingGroupAbove_CombinedWithARowArrivingBelow_AtFocusLevel_NeverStopsFollowing() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel { ReadingLevel = ReadingLevel.Focus };
        session.Transcript.Clear();
        for (var index = 0; index < 30; index++)
        {
            session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, $"row {index}"));
        }

        var window = new Window { Width = 700, Height = 500, Content = new SessionView { DataContext = session } };
        window.Show();
        _Settle(window);

        var scroll = _Transcript(window);
        var jumpButton = _JumpToNewestButton(window);
        for (var attempt = 0; attempt < 10 && jumpButton.IsVisible; attempt++)
        {
            scroll.ScrollToEnd();
            _Settle(window);
        }

        Assert.False(jumpButton.IsVisible, "the fixture must start parked at the newest row");

        // The gap is asserted as an exact 0, not "within a few px": the defect this guards produced a stable
        // 64px gap that a tolerant assertion would have waved through, and the offset the SUT lands on is a
        // whole-pixel layout result, not an accumulating float.
        void AssertParkedAtTheBottom(string where)
        {
            Assert.False(jumpButton.IsVisible, $"{where}: no user ever scrolled, so following must not stop");
            Assert.Equal(0.0, scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y);
        }

        for (var run = 0; run < 3; run++)
        {
            var first = new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "ran something")
            {
                ToolName = "Bash", ToolUseId = $"run{run}-1", InputJson = "{}",
            };
            session.Transcript.Add(first);
            _Settle(window);
            AssertParkedAtTheBottom($"run {run}, after the first tool call");

            var second = new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "ran something else")
            {
                ToolName = "Read", ToolUseId = $"run{run}-2", InputJson = "{}",
            };
            session.Transcript.Add(second);
            _Settle(window); // must not throw "Infinite layout loop detected" either

            Assert.True(first.IsGroupAnchor, $"run {run}: the fold must have formed");
            Assert.True(second.IsGroupMember, $"run {run}: the second row must have joined it");
            AssertParkedAtTheBottom($"run {run}, after the fold");

            session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, $"done with run {run}"));
            _Settle(window);
            AssertParkedAtTheBottom($"run {run}, after the row that follows the fold");
        }

        window.Close();
    });
}
