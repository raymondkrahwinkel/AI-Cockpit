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
using Cockpit.Core.Usage;
using Cockpit.Plugins.Abstractions.Workspaces;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The host putting the run onto the session it embeds (AC-251). This is the join the usage trail depends on: the
/// embedder names its run on the request, and unless the host carries that onto the session, every record the
/// session writes comes out belonging to no run — which is the state this ticket exists to end.
/// </summary>
[Collection("avalonia")]
public class EmbeddedSessionRunTaggingTests
{
    [Fact]
    public void Embed_CarriesTheRequestsRun_OntoTheSession() => Dispatcher.UIThread.Invoke(() =>
    {
        var (cockpit, sessions) = _Cockpit();

        cockpit.Embed("workspace.autopilot.plan", new EmbeddedSessionRequest
        {
            RunId = "run-7",
            RunLabel = "AC-251 - persist usage",
        });

        var session = Assert.Single(sessions);
        Assert.Equal(UsageRunKind.Embedded, session.RunKind);
        Assert.Equal("run-7", session.RunId);
        Assert.Equal("AC-251 - persist usage", session.RunLabel);
    });

    [Fact]
    public void Embed_WithoutARun_StillMarksTheSessionAsEmbedded() => Dispatcher.UIThread.Invoke(() =>
    {
        var (cockpit, sessions) = _Cockpit();

        cockpit.Embed("workspace.autopilot.plan", new EmbeddedSessionRequest());

        // A plugin that embeds a session for no particular run — the planning round — still spends, and its
        // records must read as plugin work rather than as something the operator typed.
        var session = Assert.Single(sessions);
        Assert.Equal(UsageRunKind.Embedded, session.RunKind);
        Assert.Null(session.RunId);
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
