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

    [Theory]
    [InlineData("Decode the UTF-8 payload before comparing, see AC-544.")]
    [InlineData("Switch the hash to SHA-256 across the board.")]
    [InlineData("Port the prompt from GPT-4 to the local model.")]
    [InlineData("Follow RFC-2119 for the wording of the new options.")]
    public void Embed_WithAVersionLikeTokenBeforeAnyTicket_SeedsNothingRatherThanTheWrongThing(string brief) =>
        Dispatcher.UIThread.Invoke(() =>
        {
            // The pattern that finds "AC-544" also finds "UTF-8" and "SHA-256". Unanchored it took the first match
            // anywhere in the brief, so a brief that merely mentions an encoding seeded a statusline claiming the
            // session was working on it — which the assistant then reads back to the operator as fact. Seeding
            // nothing is the right way to be wrong: a blank line reads as "has not said", a wrong one reads as true.
            var (cockpit, sessions) = _Cockpit();

            cockpit.Embed("workspace.autopilot.plan", new EmbeddedSessionRequest { InitialUserMessage = brief });

            Assert.Equal(string.Empty, Assert.Single(sessions).Statusline);
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
