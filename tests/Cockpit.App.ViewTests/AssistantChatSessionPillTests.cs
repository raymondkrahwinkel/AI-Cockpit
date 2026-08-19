using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;
using Cockpit.Core.UsagePill;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-776 Deel 1: the session-status pill in the chat window's header — one segment per live session next to the
/// usage pill, breaking onto its own line (A5a's <c>WrapPanel</c>) when the window is too narrow for both.
/// </summary>
[Collection("avalonia")]
public sealed class AssistantChatSessionPillTests
{
    private static CockpitViewModel _Cockpit()
    {
        var cockpit = new CockpitViewModel();
        cockpit.Sessions.Clear();
        return cockpit;
    }

    private static SessionViewModel _Session(string paneId, string title, SessionStatus status = SessionStatus.Idle)
    {
        var session = new SessionViewModel { Title = title };
        session.AdoptPaneId(paneId);
        session.SessionStatus = status;
        return session;
    }

    private static AssistantChatWindow _Window(CockpitViewModel cockpit, int width = 520, SessionViewModel? assistantSession = null)
    {
        var host = Substitute.For<IAssistantSessionHost>();
        host.Session.Returns(assistantSession);
        var store = Substitute.For<IAssistantSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new AssistantSettings { IsEnabled = true }));

        var window = new AssistantChatWindow
        {
            Width = width,
            Height = 560,
            DataContext = new AssistantChatViewModel(host, store, Substitute.For<IVoicePlaybackQueue>(), cockpit: cockpit),
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    // The usage pill next to the session pill (both live in the same WrapPanel, AC-776) needs a reported figure
    // to show at all — see UsagePillUnreportedWindowTests' own remarks on why a bare SessionViewModel stays empty.
    private static SessionViewModel _AssistantSessionWithUsagePill()
    {
        var session = new SessionViewModel { UsagePillVisibleFields = [UsagePillField.Context] };
        session.ContextUsedPercent = 50;
        return session;
    }

    [Fact]
    public void WithNoLiveSessions_ThePillStaysHidden() => HeadlessAvalonia.Run(() =>
    {
        var window = _Window(_Cockpit());
        try
        {
            var pill = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SessionPill");
            Assert.False(pill.IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void OneSegmentPerLiveSession_ShowsTheDotColourAndTheTrimmedName() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = _Cockpit();
        cockpit.Sessions.Add(_Session("s1", "AC-774", SessionStatus.Busy));
        cockpit.Sessions.Add(_Session("s2", "depot-fix", SessionStatus.Done));
        var window = _Window(cockpit);
        try
        {
            var pill = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SessionPill");
            Assert.True(pill.IsEffectivelyVisible);

            var segments = window.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "SessionSegments");
            Assert.Equal(2, segments.ItemCount);

            var names = window.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Text is "AC-774" or "depot-fix").Select(t => t.Text).ToList();
            Assert.Contains("AC-774", names);
            Assert.Contains("depot-fix", names);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>Criterion 6: the tooltip carries the full name, status + desk, and the statusline — resolved
    /// through the header's own compiled binding back to the view model (the desk name lives in
    /// AssistantChatViewModel.DeskNameByPaneId, not on SessionPanelViewModel itself).</summary>
    [Fact]
    public void ASessionSegment_CarriesAThreeLineTooltip_WithTheDeskNameResolved() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = _Cockpit();
        var session = _Session("s1", "AC-774", SessionStatus.Busy);
        session.Statusline = "reviewing the diff";
        cockpit.Sessions.Add(session);
        var window = _Window(cockpit);
        try
        {
            var nameBlock = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "AC-774");
            var segment = nameBlock.FindAncestorOfType<StackPanel>(includeSelf: false);
            // Walk to the outer StackPanel per session (the one carrying ToolTip.Tip) — the immediate parent is the
            // inner name/dot StackPanel, its parent is the tooltip-bearing one.
            var row = segment!.FindAncestorOfType<StackPanel>(includeSelf: false);
            var tip = Avalonia.Controls.ToolTip.GetTip(row!) as string;

            Assert.NotNull(tip);
            Assert.StartsWith("AC-774\nBusy · Sessions\nreviewing the diff", tip);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>Criterion 4/5: the "⋯" segment is a real Button with a real Flyout — not a bare TextBlock sharing
    /// the pill's own Border (pitfall 2, needed so the flyout gets its own popup layer) — and that flyout's
    /// content is the session list, one row per live session.</summary>
    [Fact]
    public void TheSessionListButton_CarriesAFlyoutListingEverySession() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = _Cockpit();
        cockpit.Sessions.Add(_Session("s1", "AC-774", SessionStatus.Busy));
        cockpit.Sessions.Add(_Session("s2", "depot-fix", SessionStatus.Done));
        var window = _Window(cockpit);
        try
        {
            var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SessionListButton");
            var flyout = Assert.IsType<Flyout>(button.Flyout);
            // Exactly one ItemsControl in the flyout's content — the session list, bound to LiveSessions in AXAML
            // (the same collection and #Root-based desk lookup the pill's own tooltip, proven above, already reads).
            Assert.Single(Assert.IsType<StackPanel>(flyout.Content).Children.OfType<ItemsControl>());
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>AC-895 criteria 1/4: clicking a session badge (anywhere on its row, not just the trimmed name)
    /// selects that session on the shared <see cref="CockpitViewModel"/> — the same
    /// <see cref="CockpitViewModel.SelectSessionCommand"/> the sidebar uses, reached here through
    /// <see cref="AssistantChatViewModel"/>'s thin passthrough. Raised directly on the segment (the
    /// <c>MarkdownBlockReuseTests</c> idiom) rather than via a window-coordinate <c>MouseDown</c>: this window's
    /// Fluent backdrop layer hit-tests in front of its own content in the headless harness, which a direct
    /// <c>RaiseEvent</c> — routed through the segment's real visual ancestry, not a screen-point hit test — sidesteps
    /// entirely. The window-activation half (criterion 2) is not exercised here — the headless harness has no
    /// <c>IClassicDesktopStyleApplicationLifetime</c>, the same gap <see cref="DialogModalitySplitTests"/> notes for
    /// <c>SessionDialogService</c>.</summary>
    [Fact]
    public void ClickingASessionSegment_SelectsThatSessionOnTheCockpit() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = _Cockpit();
        var session = _Session("s1", "AC-774", SessionStatus.Busy);
        cockpit.Sessions.Add(session);
        var window = _Window(cockpit);
        try
        {
            var nameBlock = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "AC-774");
            var innerRow = nameBlock.FindAncestorOfType<StackPanel>(includeSelf: false);
            var segment = innerRow!.FindAncestorOfType<StackPanel>(includeSelf: false);

            var pointer = new Pointer(0, PointerType.Mouse, isPrimary: true);
            var properties = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
            segment!.RaiseEvent(new PointerPressedEventArgs(
                segment, pointer, window, new Point(segment.Bounds.Width / 2, segment.Bounds.Height / 2), 0, properties, KeyModifiers.None));

            Assert.Same(session, cockpit.SelectedSession);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>
    /// AC-949: clicking a row in the Sessions flyout selects that session and closes the flyout, the same as
    /// clicking a session badge does.
    /// </summary>
    [Fact]
    public void ClickingAFlyoutRow_SelectsThatSessionAndClosesTheFlyout() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = _Cockpit();
        var session = _Session("s1", "AC-774", SessionStatus.Busy);
        cockpit.Sessions.Add(session);
        var window = _Window(cockpit);
        try
        {
            var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SessionListButton");
            var flyout = (Flyout)button.Flyout!;
            flyout.ShowAt(button);
            Dispatcher.UIThread.RunJobs();

            var row = ((Control)flyout.Content!).GetLogicalDescendants().OfType<TextBlock>()
                .Single(t => t.Text == "AC-774").FindAncestorOfType<StackPanel>(includeSelf: false)!;

            var pointer = new Pointer(0, PointerType.Mouse, isPrimary: true);
            var properties = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
            row.RaiseEvent(new PointerPressedEventArgs(
                row, pointer, window, new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), 0, properties, KeyModifiers.None));
            Dispatcher.UIThread.RunJobs();

            Assert.Same(session, cockpit.SelectedSession);
            Assert.False(flyout.IsOpen);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>Criterion 7/8: at the window's 340px floor the session pill cannot sit next to the usage pill and
    /// wraps onto its own line — and the wrap goes away again once the window is wide enough for both.</summary>
    [Fact]
    public void ANarrowWindow_WrapsTheSessionPillOntoItsOwnLine_AndUndoesItWhenWidened() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = _Cockpit();
        cockpit.Sessions.Add(_Session("s1", "a-fairly-long-session-name-one"));
        cockpit.Sessions.Add(_Session("s2", "a-fairly-long-session-name-two"));
        cockpit.Sessions.Add(_Session("s3", "a-fairly-long-session-name-three"));
        var window = _Window(cockpit, width: 340, assistantSession: _AssistantSessionWithUsagePill());
        try
        {
            var usagePillRow = window.GetVisualDescendants().OfType<ItemsControl>()
                .Single(c => c.Name == "SessionSegments").GetVisualAncestors().OfType<Border>()
                .Single(b => b.Name == "SessionPill");
            var sessionsTop = usagePillRow.Bounds.Y;

            window.Width = 900;
            Dispatcher.UIThread.RunJobs();
            var sessionsTopWide = usagePillRow.Bounds.Y;

            // Narrow: the session pill has wrapped below the header/usage-pill row, so its own Y sits well below
            // the top of the window. Wide: both pills fit on one line, so the wrap collapses and Y drops back.
            Assert.True(sessionsTopWide < sessionsTop, $"expected the session pill to move back up once widened (was {sessionsTop}, now {sessionsTopWide})");
        }
        finally
        {
            window.Close();
        }
    });
}
