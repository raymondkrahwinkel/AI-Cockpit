using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
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
}
