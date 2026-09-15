using Cockpit.App.Services;
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

    // WithNoLiveSessions_ThePillHasNothingToShow stood here. Its whole body — a fresh view model over an empty
    // cockpit, Empty(LiveSessions) and False(HasLiveSessions) — is the opening of the test below, on the same two
    // objects built the same way; and HasLiveSessions is LiveSessions.Count > 0, so the two assertions are one.
    [Fact]
    public void ASessionStartingOrStopping_UpdatesLiveSessionsWithoutReopening() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = _Cockpit();
        var vm = _Vm(cockpit);
        Assert.False(vm.HasLiveSessions);

        var session = _Session("s1", "depot-fix");
        cockpit.Sessions.Add(session);
        Assert.Equal(["s1"], vm.LiveSessions.Select(s => s.PaneId));
        Assert.True(vm.HasLiveSessions);

        cockpit.Sessions.Remove(session);
        Assert.Empty(vm.LiveSessions);
        Assert.False(vm.HasLiveSessions);
    });

    [Fact]
    public void DeskNameByPaneId_ResolvesAnUnassignedSessionToTheFirstWorkspace() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = _Cockpit();
        cockpit.Sessions.Add(_Session("s1", "AC-774"));

        var vm = _Vm(cockpit);

        Assert.Equal("Sessions", vm.DeskNameByPaneId["s1"]);
    });

    // AC-1300: the two sessions match on everything the old "lives and is visible" rule saw; only who started them differs.
    [Fact]
    public void SessionsStartedByTheAssistant_HoldsOnlyThoseTheAssistantStarted() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = _Cockpit();
        cockpit.Sessions.Add(_Session("spawned", "AC-774", startedByTheAssistant: true));
        cockpit.Sessions.Add(_Session("operator-opened", "AC-774"));

        var vm = _Vm(cockpit);

        Assert.Equal(["spawned", "operator-opened"], vm.LiveSessions.Select(session => session.PaneId));
        Assert.Equal(["spawned"], vm.SessionsStartedByTheAssistant.Select(session => session.PaneId));
    });

    // AC-1300 criterion 2: the relation lives on the session, so a chat window built fresh over the same cockpit still finds it.
    [Fact]
    public void SessionsStartedByTheAssistant_SurvivesAFreshChatWindowAndDropsAClosedSession() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = _Cockpit();
        var session = _Session("spawned", "AC-774", startedByTheAssistant: true);
        cockpit.Sessions.Add(session);

        _Vm(cockpit).Dispose();
        var reopened = _Vm(cockpit);
        Assert.Equal(["spawned"], reopened.SessionsStartedByTheAssistant.Select(live => live.PaneId));

        cockpit.Sessions.Remove(session);
        Assert.Empty(reopened.SessionsStartedByTheAssistant);
    });

    // AC-1300 criterion 4: asserted on the resolver — the collection drops the assistant a step earlier and would pass either way.
    [Fact]
    public void AssistantSessionOrigin_NeverPutsTheAssistantUnderItself()
    {
        var assistant = new SessionViewModel { BelongsToNoWorkspace = true, StartedByTheAssistant = true };

        Assert.False(AssistantSessionOrigin.Resolve(assistant));
    }

    // AC-774 again: the live-session subscription must come off on close, or every reopened chat window chains another handler.
    [Fact]
    public void Dispose_StopsFollowingTheCockpitsSessionList() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = _Cockpit();
        var vm = _Vm(cockpit);

        vm.Dispose();
        cockpit.Sessions.Add(_Session("s1", "depot-fix"));

        Assert.Empty(vm.LiveSessions);
        Assert.False(vm.HasLiveSessions);
    });
}
