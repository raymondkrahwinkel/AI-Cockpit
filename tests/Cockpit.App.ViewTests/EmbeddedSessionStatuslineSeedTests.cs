using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Plugins.Abstractions.Workspaces;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The host seeding a freshly embedded session's statusline from the ticket already sitting in its brief (AC-544),
/// deterministically and before the session's own agent ever gets a turn to call <c>set_status</c> itself — so a
/// step whose model never calls it, or dies on its first turn, still shows a statusline. Checked against
/// <see cref="CockpitViewModel.Embed"/>, the one seam every embedded spawn (Autopilot's step brief, its run label)
/// goes through.
/// </summary>
[Collection("avalonia")]
public class EmbeddedSessionStatuslineSeedTests
{
    [Fact]
    public void Embed_WithATicketInTheOpeningBrief_SeedsTheStatuslineFromIt() => Dispatcher.UIThread.Invoke(() =>
    {
        var (cockpit, sessions) = _Cockpit();

        cockpit.Embed("workspace.autopilot.plan", new EmbeddedSessionRequest
        {
            InitialUserMessage = "AC-544 — seed the statusline host-side before the agent ever calls set_status.",
        });

        var session = Assert.Single(sessions);
        Assert.Equal("AC-544", session.Statusline);
    });

    [Fact]
    public void Embed_WithATicketOnlyInTheRunLabel_SeedsTheStatuslineFromThat() => Dispatcher.UIThread.Invoke(() =>
    {
        var (cockpit, sessions) = _Cockpit();

        cockpit.Embed("workspace.autopilot.plan", new EmbeddedSessionRequest
        {
            RunLabel = "AC-544 - statusline seeding",
            InitialUserMessage = "Do the work.",
        });

        var session = Assert.Single(sessions);
        Assert.Equal("AC-544", session.Statusline);
    });

    [Fact]
    public void Embed_WithNoTicketAnywhereInTheBrief_LeavesTheStatuslineBlank() => Dispatcher.UIThread.Invoke(() =>
    {
        var (cockpit, sessions) = _Cockpit();

        cockpit.Embed("workspace.autopilot.plan", new EmbeddedSessionRequest
        {
            RunLabel = "CEO planning round",
            InitialUserMessage = "Draft a plan for the operator's request.",
        });

        // No made-up ticket beats an invented one — the field stays exactly what SessionPanelViewModel opens on.
        var session = Assert.Single(sessions);
        Assert.Equal(string.Empty, session.Statusline);
    });

    // A cockpit with just enough graph to embed, and the sessions its factory handed out.
    private static (CockpitViewModel Cockpit, List<SessionViewModel> Sessions) _Cockpit()
    {
        var sessions = new List<SessionViewModel>();
        var cockpit = new CockpitViewModel(
            () =>
            {
                var session = new SessionViewModel();
                sessions.Add(session);
                return session;
            },
            () => new TtyViewModel(),
            Substitute.For<ISessionDialogService>(),
            Substitute.For<IAudioCaptureService>(),
            Substitute.For<IAudioPlaybackService>(),
            Substitute.For<IAttentionNotifier>(),
            Substitute.For<INotificationSettingsStore>(),
            Substitute.For<ITranscriptDisplaySettingsStore>(),
            Substitute.For<ISessionBehaviorSettingsStore>(),
            Substitute.For<ILayoutSettingsStore>(),
            Substitute.For<IVoiceSettingsStore>(),
            Substitute.For<ITerminalSettingsStore>(),
            sessionProfileStore: Substitute.For<ISessionProfileStore>());

        return (cockpit, sessions);
    }
}
