using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

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
}
