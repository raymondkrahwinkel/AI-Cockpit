using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Sessions;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-996. The consent card is a transcript row, so needs-attention pointed at nothing whenever the transcript was
/// scrolled somewhere else: same permission, same status, and the card either in view or not depending only on where
/// the operator had scrolled. Not solved by scrolling the card in — scrolled up is the operator reading history, a
/// distinction AC-459/AC-621 bought over four rounds — so the corner button gains a second destination, and these
/// cases hold it to naming what waits and reaching it from anywhere.
/// </summary>
[Collection("avalonia")]
public class PendingPermissionReachabilityTests
{
    private const string JumpToNewestTip = "Jump to the newest message";
    private const string ApprovalWaitingTip = "A tool is waiting for your approval — jump to it";

    private static ScrollViewer _Transcript(Visual root) =>
        root.GetVisualDescendants().OfType<ScrollViewer>().First(scroll => scroll.Name == "TranscriptScroll");

    private static Button _JumpButton(Visual root) =>
        root.GetVisualDescendants().OfType<Button>().First(button => button.Name == "ScrollToBottomButton");

    // Off the view's own field, not the visual tree: a hidden button never realises its content, and half of
    // these cases are about the button being hidden.
    private static MaterialIcon _JumpIcon(Window window) =>
        ((SessionView)window.Content!).ScrollToBottomIcon;

    private static void _Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static (Window Window, SessionView View, SessionViewModel Session, ScrollViewer Scroll) _PaneParkedAtTheBottom()
    {
        var session = new SessionViewModel();
        for (var index = 0; index < 60; index++)
        {
            session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, $"row {index}"));
        }

        var view = new SessionView { DataContext = session };
        var window = new Window { Width = 700, Height = 500, Content = view };
        window.Show();
        _Settle(window);

        var scroll = _Transcript(window);
        scroll.ScrollToEnd();
        _Settle(window);
        Assert.False(_JumpButton(window).IsVisible, "the fixture must start parked at the newest row");

        return (window, view, session, scroll);
    }

    /// <summary>The operator rolling up through the history — a real gesture that really moves the viewport.</summary>
    private static void _ScrollUpToRead(Window window)
    {
        var scroll = _Transcript(window);
        var parked = scroll.Offset.Y;
        for (var click = 0; click < 12; click++)
        {
            window.MouseWheel(new Point(window.Width / 2, window.Height / 3), new Vector(0, 1));
            _Settle(window);
        }

        Assert.True(scroll.Offset.Y < parked, "the fixture must actually have left the tail");
    }

    private static void _AskForPermission(SessionViewModel session, string toolUseId = "toolu_1")
    {
        session.Apply(new ToolUseRequested
        {
            SessionId = "S1", ToolUseId = toolUseId, ToolName = "list_agents", InputJson = "{}",
        });
        session.Apply(new PermissionRequested
        {
            SessionId = "S1", ToolUseId = toolUseId, ToolName = "list_agents", InputJson = "{}",
        });
    }

    private static bool _ConsentCardIsInView(Window window, SessionViewModel session)
    {
        var items = window.GetVisualDescendants().OfType<ItemsControl>().First(control => control.Name == "TranscriptItems");
        var card = session.VisibleTranscript.Last(entry => entry.IsPendingPermission);
        var index = session.VisibleTranscript.IndexOf(card);
        if (index < 0 || items.ContainerFromIndex(index) is not { } container)
        {
            return false;
        }

        var scroll = _Transcript(window);
        var top = container.TranslatePoint(new Point(0, 0), scroll);
        return top is { } point && point.Y >= -1 && point.Y < scroll.Viewport.Height;
    }

    /// <summary>
    /// The reported case. Needs-attention with the card below the fold must offer a way to it — the button names
    /// what is waiting instead of "the newest message", which is the half that was missing.
    /// </summary>
    [Fact]
    public void APermissionLandingWhileScrolledUp_IsStillReachable() => HeadlessAvalonia.Run(() =>
    {
        var (window, _, session, _) = _PaneParkedAtTheBottom();
        _ScrollUpToRead(window);

        _AskForPermission(session);
        _Settle(window);

        Assert.Equal(SessionStatus.NeedsAttention, session.SessionStatus);
        Assert.False(_ConsentCardIsInView(window, session), "the premise: reading history leaves the card off screen");

        Assert.True(_JumpButton(window).IsVisible);
        Assert.Equal(ApprovalWaitingTip, ToolTip.GetTip(_JumpButton(window)));
        Assert.Equal(MaterialIconKind.ShieldAlertOutline, _JumpIcon(window).Kind);

        window.Close();
    });

    /// <summary>And the way there has to arrive somewhere: one click puts the card on screen.</summary>
    [Fact]
    public void ClickingThatWayThrough_BringsTheConsentCardOnScreen() => HeadlessAvalonia.Run(() =>
    {
        var (window, _, session, _) = _PaneParkedAtTheBottom();
        _ScrollUpToRead(window);
        _AskForPermission(session);
        _Settle(window);

        _JumpButton(window).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        _Settle(window);

        Assert.True(_ConsentCardIsInView(window, session), "one click on the affordance must reach the card");
        Assert.False(_JumpButton(window).IsVisible, "and the affordance is spent once the card is there");

        window.Close();
    });

    /// <summary>
    /// The render clock taken away (AC-883) dematerialises every row, so nothing is reachable by looking even
    /// while the follow is nominally on — the button has to say so rather than hide because `_stickToBottom` is
    /// still true.
    /// </summary>
    [Fact]
    public void APermissionLandingWhileTheRenderClockIsPaused_IsStillReachable() => HeadlessAvalonia.Run(() =>
    {
        var (window, view, session, _) = _PaneParkedAtTheBottom();

        view.SetRenderClockPaused(true);
        _Settle(window);
        _AskForPermission(session);
        _Settle(window);

        Assert.Equal(SessionStatus.NeedsAttention, session.SessionStatus);
        Assert.True(_JumpButton(window).IsVisible, "a paused pane shows nothing, so the way in must still be offered");
        Assert.Equal(ApprovalWaitingTip, ToolTip.GetTip(_JumpButton(window)));

        // Resuming follows the tail again (the pane never left it), and that is where the card is.
        view.SetRenderClockPaused(false);
        _Settle(window);
        Assert.True(_ConsentCardIsInView(window, session));
        Assert.False(_JumpButton(window).IsVisible);

        window.Close();
    });

    /// <summary>
    /// Criterion 5: a session that shows its consent normally is untouched — no button, no alarm icon, and the
    /// tooltip still the one #21 put there.
    /// </summary>
    [Fact]
    public void APermissionLandingWhileParkedAtTheBottom_ChangesNothing() => HeadlessAvalonia.Run(() =>
    {
        var (window, _, session, _) = _PaneParkedAtTheBottom();

        _AskForPermission(session);
        _Settle(window);

        Assert.True(_ConsentCardIsInView(window, session));
        Assert.False(_JumpButton(window).IsVisible);
        Assert.Equal(JumpToNewestTip, ToolTip.GetTip(_JumpButton(window)));
        Assert.Equal(MaterialIconKind.ChevronDown, _JumpIcon(window).Kind);

        window.Close();
    });

    /// <summary>
    /// And a pane scrolled up with nothing pending keeps the plain jump-to-newest it has had since #21 — the
    /// second destination must not swallow the first.
    /// </summary>
    [Fact]
    public void ScrolledUpWithNothingPending_KeepsThePlainJumpToNewest() => HeadlessAvalonia.Run(() =>
    {
        var (window, _, _, _) = _PaneParkedAtTheBottom();
        _ScrollUpToRead(window);

        Assert.True(_JumpButton(window).IsVisible);
        Assert.Equal(JumpToNewestTip, ToolTip.GetTip(_JumpButton(window)));
        Assert.Equal(MaterialIconKind.ChevronDown, _JumpIcon(window).Kind);

        window.Close();
    });
}
