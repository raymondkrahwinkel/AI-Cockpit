using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;
using NSubstitute;

namespace Cockpit.App.ViewTests;

// AC-776: built off the live CockpitViewModel the sidebar reads, with the filter AssistantReadGateway._ListSessions applies.
[Collection("avalonia")]
public sealed class AssistantChatLiveSessionsTests
{
    private static IAssistantSessionHost _FakeHost() => Substitute.For<IAssistantSessionHost>();

    private static IAssistantSettingsStore _FakeSettingsStore()
    {
        var store = Substitute.For<IAssistantSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new AssistantSettings { IsEnabled = true }));
        return store;
    }

    private static AssistantChatViewModel _Vm(CockpitViewModel cockpit) =>
        new(_FakeHost(), _FakeSettingsStore(), Substitute.For<IVoicePlaybackQueue>(), cockpit: cockpit);

    // The parameterless constructor is the previewer's, like `SessionViewModel()` — it seeds sample sessions so
    // a design-time canvas has something to show; a real cockpit starts with none.
    private static CockpitViewModel _Cockpit()
    {
        var cockpit = new CockpitViewModel();
        cockpit.Sessions.Clear();
        return cockpit;
    }

    private static SessionViewModel _Session(
        string paneId, string title, bool showPluginHeaderItems = true, bool startedByTheAssistant = false)
    {
        var session = new SessionViewModel { Title = title, StartedByTheAssistant = startedByTheAssistant };
        session.AdoptPaneId(paneId);
        session.ShowPluginHeaderItems = showPluginHeaderItems;
        return session;
    }

    [Fact]
    public void LiveSessions_ExcludesTheAssistantsOwnSessionAndPlainTerminals() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = _Cockpit();
        cockpit.Sessions.Add(_Session("s1", "AC-774"));
        cockpit.Sessions.Add(_Session("terminal", "bash", showPluginHeaderItems: false));
        cockpit.Sessions.Add(_Session(AssistantIdentity.PaneId, "assistant"));

        var vm = _Vm(cockpit);

        Assert.Equal(["s1"], vm.LiveSessions.Select(session => session.PaneId));
        Assert.True(vm.HasLiveSessions);
    });
}
