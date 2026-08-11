using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

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

    /// <summary>A session with enough rows to overflow the window, parked at the newest one.</summary>
    private static (Window Window, SessionViewModel Session, ScrollViewer Scroll, Button JumpButton) _PaneParkedAtTheBottom(
        int width = 620, int height = 480)
    {
        var session = new SessionViewModel();
        for (var index = 0; index < 60; index++)
        {
            session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, $"row {index}"));
        }

        var window = new Window { Width = width, Height = height, Content = new SessionView { DataContext = session } };
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
    /// <c>IsBusy</c> many times over (AC-459) — and asserts the follow never breaks: the jump-to-newest
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
        _AssertTogglingNeverStopsFollowing("the Thinking indicator", (session, on) => session.IsBusy = on));

    [Fact]
    public void TheStartingBanner_AppearingWhileParkedAtTheBottom_DoesNotStopFollowing() => HeadlessAvalonia.Run(() =>
        _AssertTogglingNeverStopsFollowing("the starting banner", (session, on) => session.IsStarting = on));

    private static readonly PluginUsageSignal _ContextSignal =
        new("context", "ctx", PluginUsageSignalKind.Fill, 50) { Description = "Context window" };

    [Fact]
    public void TheUsageWarningBar_AppearingWhileParkedAtTheBottom_DoesNotStopFollowing() => HeadlessAvalonia.Run(() =>
        _AssertTogglingNeverStopsFollowing(
            "the usage-warning bar",
            // AC-683: UsageWarning is now derived from Warnings, so a crossing raises it and a drop-back clears
            // it, the same real path the app itself uses — no more assigning the derived property directly.
            (session, on) => session.ApplyUsage([_ContextSignal], [new PluginUsageReading("context", on ? 88 : 4, null)])));

    [Fact]
    public void ThePendingResumeBar_AppearingWhileParkedAtTheBottom_DoesNotStopFollowing() => HeadlessAvalonia.Run(() =>
        _AssertTogglingNeverStopsFollowing(
            "the pending-resume bar", (session, on) => session.PendingResumeLabel = on ? "Resuming Mon 07:30" : string.Empty));

    // The test that used to sit here assigned scroll.Offset directly and called that "a real user scroll". It is
    // gone rather than relaxed, because the fix for AC-528 makes its premise false on purpose: an assignment to
    // Offset is indistinguishable, by the delta fields alone, from the virtualising panel correcting its own
    // offset after arrange — and reading the second as the first is what stopped the follow with nobody having
    // scrolled. What it meant to guard is now guarded through actual wheel input, which is strictly more than it
    // asked: see WheelingUpWhileStreaming_StaysPut_AndWheelingBackDownResumesFollowing.

    /// <summary>Turns of the wheel over the transcript — the operator's own gesture, not an assignment to Offset.</summary>
    private static void _WheelTicks(Window window, int deltaY, int count)
    {
        for (var tick = 0; tick < count; tick++)
        {
            window.MouseWheel(new Point(window.Width / 2, window.Height / 3), new Vector(0, deltaY));
            _Settle(window);
        }
    }

    /// <summary>
    /// AC-528 criteria 1-4, through real wheel input rather than an assignment to <c>Offset</c>: the operator
    /// wheels up while rows keep streaming in (the view must stay where they left it), and wheels back down
    /// (the follow must resume on its own, with no click on the chevron).
    /// </summary>
    [Theory]
    [InlineData(700, 470)]
    [InlineData(700, 500)]
    [InlineData(700, 560)]
    [InlineData(900, 530)]
    public void WheelingUpWhileStreaming_StaysPut_AndWheelingBackDownResumesFollowing(int width, int height) =>
        HeadlessAvalonia.Run(() =>
    {
        var (window, session, scroll, jumpButton) = _PaneParkedAtTheBottom(width, height);

        _WheelTicks(window, deltaY: 1, count: 3);
        Assert.True(jumpButton.IsVisible, "wheeling up must stop the follow");

        // Rows keep arriving while the operator reads. The view must not be dragged along with them.
        var restingOffset = scroll.Offset.Y;
        for (var streamed = 0; streamed < 5; streamed++)
        {
            session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, $"streamed {streamed}"));
            _Settle(window);
        }

        Assert.Equal(restingOffset, scroll.Offset.Y);
        Assert.True(jumpButton.IsVisible, "content arriving is not the operator returning to the tail");

        // And back down the same way — no click on the chevron. The count is generous because a wheel tick is
        // worth a fixed number of pixels and the rows that streamed in above put the tail further away.
        for (var tick = 0; tick < 60 && jumpButton.IsVisible; tick++)
        {
            _WheelTicks(window, deltaY: -1, count: 1);
        }

        Assert.False(jumpButton.IsVisible, "wheeling back to the bottom must resume the follow on its own");

        window.Close();
    });

    /// <summary>
    /// AC-528 criterion 5. A folded run at the Focus level is the case the estimate goes wrong on: a
    /// <see cref="VirtualizingStackPanel"/> builds <c>Extent</c> from the rows it happens to have realised, so
    /// <c>ScrollToEnd()</c> aims at a bottom that the panel's next arrange moves out from under it — measured at
    /// these four sizes, that left the transcript some 300px short and the newest row half under the composer
    /// hairline. Asked of the panel rather than of <c>Extent</c>: a transcript that is truly parked does not move
    /// when asked to scroll far past the end.
    /// <para>
    /// Four window sizes rather than one, because row height decides whether the estimate happens to land exactly
    /// — and row height is font, so a single size guards one machine. An earlier round of this ticket passed on
    /// the author's fonts and failed on CI's for precisely that reason.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(700, 470)]
    [InlineData(700, 500)]
    [InlineData(700, 560)]
    [InlineData(900, 530)]
    public void AFoldedRunStreamingIn_KeepsTheTranscriptParkedOnTheNewestRow(int width, int height) =>
        HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel { ReadingLevel = ReadingLevel.Focus };
        for (var index = 0; index < 30; index++)
        {
            session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, $"row {index}"));
        }

        var window = new Window { Width = width, Height = height, Content = new SessionView { DataContext = session } };
        window.Show();
        _Settle(window);

        var scroll = _Transcript(window);
        var jumpButton = _JumpToNewestButton(window);
        scroll.ScrollToEnd();
        _Settle(window);
        Assert.False(jumpButton.IsVisible, "the fixture must start parked at the newest row");

        void AssertParkedAtTheBottom(string where)
        {
            Assert.False(jumpButton.IsVisible, $"{where}: no operator ever scrolled, so the follow must not stop");

            var parked = scroll.Offset.Y;
            scroll.Offset = scroll.Offset.WithY(parked + 10_000);
            _Settle(window);
            Assert.Equal(parked, scroll.Offset.Y);
        }

        for (var run = 0; run < 3; run++)
        {
            // A run of auto tool calls arrives and folds itself into one "N steps run" line, then an ordinary
            // reply lands below it — a plain Focus-level turn, no click involved.
            session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, $"Read {run}")
            {
                IsInGroup = true,
                IsGroupAnchor = true,
                GroupCount = 4,
            });
            _Settle(window);

            for (var member = 0; member < 3; member++)
            {
                session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, $"Bash {run}.{member}")
                {
                    IsInGroup = true,
                });
                _Settle(window);
            }

            session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, $"reply {run}"));
            _Settle(window);

            AssertParkedAtTheBottom($"run {run}");
        }

        window.Close();
    });

    /// <summary>AC-528 criterion 4: the chevron stays a manual jump back to the newest row.</summary>
    [Fact]
    public void TheChevron_JumpsBackToTheNewestRow() => HeadlessAvalonia.Run(() =>
    {
        var (window, _, scroll, jumpButton) = _PaneParkedAtTheBottom();

        _WheelTicks(window, deltaY: 1, count: 5);
        Assert.True(jumpButton.IsVisible);
        var scrolledAway = scroll.Offset.Y;

        jumpButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        _Settle(window);

        Assert.True(scroll.Offset.Y > scrolledAway, "the chevron must move the view back down");
        Assert.False(jumpButton.IsVisible, "and hide itself, because the follow is on again");

        window.Close();
    });

    /// <summary>
    /// AC-528 criterion 7: a tool result short enough that its own code box cannot scroll must not swallow the
    /// wheel — the transcript scrolls instead.
    /// <para>
    /// The ticket's second candidate cause was that this box (<c>SessionView.axaml</c>, MaxHeight 280) leaves its
    /// vertical scrollbar on <c>Auto</c> where <c>MarkdownView</c> sets code blocks to <c>Disabled</c>. Measured,
    /// that candidate is wrong: Avalonia chains the wheel to the parent by itself when the inner scroller has
    /// nowhere to go, so a short result already scrolls the transcript. Setting it to <c>Disabled</c> would not
    /// fix anything and would make a long tool result unreadable — it is the only way to see past its first 280px.
    /// Hence one case here, the criterion's own: a result long enough to scroll keeps the wheel, which is what a
    /// nested scroller is for, and is Avalonia's behaviour rather than this view's.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(3, true)]
    public void TheWheelOverAToolResult_ScrollsTheTranscriptOnlyWhenTheResultCannot(int resultLines, bool expectTranscriptToScroll) =>
        HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        for (var index = 0; index < 40; index++)
        {
            session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, $"row {index}"));
        }

        var result = new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "Bash") { IsExpanded = true };
        result.SetResult(string.Join("\n", Enumerable.Range(0, resultLines).Select(line => $"line {line}")), isError: false);
        session.Transcript.Add(result);

        // Rows below it as well, so the box sits inside the viewport with somewhere for the transcript to scroll
        // to — as the last row it would be pushed out of view by the very scroll that puts it in reach.
        for (var index = 40; index < 60; index++)
        {
            session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, $"row {index}"));
        }

        var window = new Window { Width = 700, Height = 500, Content = new SessionView { DataContext = session } };
        window.Show();
        _Settle(window);

        var scroll = _Transcript(window);

        // Take the follow off first: while it is on, every offset the loop below sets is a passive change that
        // the view answers by putting the newest row back in view, and the walk would never get anywhere.
        _WheelTicks(window, deltaY: 1, count: 1);

        // The tool-result code box specifically (SessionView.axaml gives it MaxHeight 280), not whatever other
        // scroller a row happens to contain. Walk down until it is realised and sitting in the viewport.
        ScrollViewer? box = null;
        Point over = default;
        for (var step = 0; step < 200 && box is null; step++)
        {
            scroll.Offset = scroll.Offset.WithY(step * 25);
            _Settle(window);

            var candidate = scroll.GetVisualDescendants().OfType<ScrollViewer>()
                .FirstOrDefault(nested => nested.MaxHeight == 280 && nested.Bounds.Height > 0);
            var centre = candidate?.TranslatePoint(new Point(candidate.Bounds.Width / 2, candidate.Bounds.Height / 2), window);
            if (centre is { } point && point.Y > 0 && point.Y < scroll.Viewport.Height)
            {
                box = candidate;
                over = point;
            }
        }

        Assert.NotNull(box);
        Assert.True(scroll.Offset.Y < scroll.Extent.Height - scroll.Viewport.Height,
            "the transcript must have room left to scroll down, or this proves nothing");

        // Downwards, because the box starts at its own top: an upward wheel would chain to the transcript
        // whether or not the box could scroll, and would prove nothing.
        var transcriptBefore = scroll.Offset.Y;
        var boxBefore = box.Offset.Y;
        window.MouseWheel(over, new Vector(0, -1));
        _Settle(window);

        if (expectTranscriptToScroll)
        {
            Assert.Equal(boxBefore, box.Offset.Y);
            Assert.NotEqual(transcriptBefore, scroll.Offset.Y);
        }
        else
        {
            Assert.NotEqual(boxBefore, box.Offset.Y);
            Assert.Equal(transcriptBefore, scroll.Offset.Y);
        }

        window.Close();
    });
}
