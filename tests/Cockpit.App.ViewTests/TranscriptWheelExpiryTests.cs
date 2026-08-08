using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-621. <c>_OnTranscriptScrollChanged</c> asks one question — did the operator cause this change? — and answers
/// it from a flag the wheel sets. A wheel turn at the bottom of the transcript scrolls nothing, so it raises no
/// <c>ScrollChanged</c>, so nothing clears that flag; the next change to arrive inherits it and is charged to an
/// operator who has since stopped touching the mouse. Sending a message is when that lands: your own row is the
/// first content change after a reading round, and a row of any size is enough for the geometry at that instant to
/// read as "scrolled away from the tail" — so the follow stops on the one action that most obviously means
/// "I want to see what happens next".
/// <para>
/// Measured on <c>89f12c82</c> before the fix, at both sizes below and identically: a four-line message left the
/// transcript 4px short of the bottom with the follow off, eight lines 62px, sixty lines 817px — and the very same
/// message with no wheel turn in front of it never broke the follow at all. That control is the second case here:
/// it is what makes this a test of the stale flag rather than of tall rows, which is AC-611's subject.
/// </para>
/// </summary>
[Collection("avalonia")]
public class TranscriptWheelExpiryTests
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

    /// <summary>
    /// Sends a message while parked at the newest row and asserts the follow survives it, optionally rolling the
    /// wheel one click further down first — the gesture that scrolls nothing, because there is nowhere left to go.
    /// </summary>
    private static void _AssertSendingKeepsFollowing(int width, int height, bool wheelFirst)
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

        if (wheelFirst)
        {
            var parked = scroll.Offset.Y;
            window.MouseWheel(new Point(window.Width / 2, window.Height / 3), new Vector(0, -1));
            _Settle(window);

            // The premise of the whole case: this turn of the wheel moved nothing, so no ScrollChanged came of it.
            // If a future Avalonia lets it move, this test stops being about a stale flag and must be rethought
            // rather than relaxed.
            Assert.Equal(parked, scroll.Offset.Y);
            Assert.False(jumpButton.IsVisible, "a wheel turn that scrolls nothing has not left the tail");
        }

        // The operator's own message. Eight lines: enough that the panel cannot absorb the growth in the single
        // pass before ScrollChanged, which is the geometry the stale flag was being asked to judge.
        session.Transcript.Add(new TranscriptEntryViewModel(
            TranscriptEntryKind.UserText,
            string.Join("\n", Enumerable.Range(0, 8).Select(line => $"line {line} of my message"))));
        _Settle(window);

        var because = wheelFirst ? "after a wheel turn that scrolled nothing" : "with no wheel turn in front of it";
        Assert.False(jumpButton.IsVisible, $"sending a message while parked at the bottom must keep the follow on, {because}");
        Assert.True(
            TranscriptScrollAnchor.IsAtBottom(scroll.Offset.Y, scroll.Extent.Height, scroll.Viewport.Height),
            $"and must leave the transcript at the bottom, {because}");

        window.Close();
    }

    [Theory]
    [InlineData(700, 470, true)]
    [InlineData(700, 500, true)]
    [InlineData(700, 560, true)]
    [InlineData(900, 530, true)]
    [InlineData(700, 470, false)]
    [InlineData(900, 530, false)]
    public void SendingAMessageWhileParkedAtTheBottom_KeepsFollowing(int width, int height, bool wheelFirst) =>
        HeadlessAvalonia.Run(() => _AssertSendingKeepsFollowing(width, height, wheelFirst));
}
