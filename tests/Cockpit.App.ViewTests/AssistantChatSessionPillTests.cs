using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;
using NSubstitute;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Tests.Shared;

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
        host.Session.Returns(TestSessions.Assistant(assistantSession));
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
}
