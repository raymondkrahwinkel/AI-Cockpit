using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-776: the session-status pill's source data — <see cref="AssistantChatViewModel.LiveSessions"/>,
/// <see cref="AssistantChatViewModel.HasLiveSessions"/> and <see cref="AssistantChatViewModel.DeskNameByPaneId"/> —
/// built off the same live <c>CockpitViewModel</c> the sidebar itself reads, with the same filter
/// <c>AssistantReadGateway._ListSessions</c> applies (live agent sessions only, never the assistant itself).
/// </summary>
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

    private static SessionViewModel _Session(string paneId, string title, bool showPluginHeaderItems = true)
    {
        var session = new SessionViewModel { Title = title };
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

    [Fact]
    public void WithNoLiveSessions_ThePillHasNothingToShow() => HeadlessAvalonia.Run(() =>
    {
        var vm = _Vm(_Cockpit());

        Assert.Empty(vm.LiveSessions);
        Assert.False(vm.HasLiveSessions);
    });

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

    /// <summary>An unassigned session (no WorkspaceId of its own) falls back to the first Sessions workspace, "Sessions" by default.</summary>
    [Fact]
    public void DeskNameByPaneId_ResolvesAnUnassignedSessionToTheFirstWorkspace() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = _Cockpit();
        cockpit.Sessions.Add(_Session("s1", "AC-774"));

        var vm = _Vm(cockpit);

        Assert.Equal("Sessions", vm.DeskNameByPaneId["s1"]);
    });

    /// <summary>AC-774's own lesson, back in this window: the subscription this view model holds on the live
    /// session list must come off on close, or every reopened chat window chains another handler onto it.</summary>
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
