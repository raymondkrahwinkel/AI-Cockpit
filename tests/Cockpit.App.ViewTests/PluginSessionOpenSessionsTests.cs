using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-833: <c>ICockpitSessionObserver.OpenSessions</c> lists every open session by pane id and operator-visible
/// name — including the assistant, which sits outside <c>CockpitViewModel.Sessions</c> by design
/// (<c>CreateAssistantSession</c>) but is still a session this surface must be able to name.
/// </summary>
[Collection("avalonia")]
public class PluginSessionOpenSessionsTests
{
    [Fact]
    public void OpenSessions_ListsEveryOrdinarySession_ByPaneIdAndTitle() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = _Cockpit();
        var one = new SessionViewModel { Title = "Session 1", ActiveProfileLabel = "work", SessionStatus = SessionStatus.Busy };
        var two = new SessionViewModel { Title = "Session 2", ActiveProfileLabel = "personal", SessionStatus = SessionStatus.Idle };
        cockpit.Sessions.Add(one);
        cockpit.Sessions.Add(two);

        var observer = new PluginSessionObserver(cockpit);

        Assert.Equal(
            new (string PaneId, string Name, string? ProfileLabel, string? Status)[]
            {
                (one.PaneId, "Session 1", "work", "Busy"),
                (two.PaneId, "Session 2", "personal", "Idle"),
            },
            observer.OpenSessions.Select(session => (session.PaneId, session.Name, session.ProfileLabel, session.Status)));
    });

    [Fact]
    public void OpenSessions_IncludesTheAssistant_UnderItsFixedPaneId() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = _Cockpit();
        var assistant = cockpit.CreateAssistantSession("cockpit-assistant");
        Assert.NotNull(assistant);

        var observer = new PluginSessionObserver(cockpit);

        Assert.Contains(observer.OpenSessions, session => session.PaneId == "cockpit-assistant");
    });

    private static CockpitViewModel _Cockpit() => new(
        () => new SessionViewModel(),
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
}
