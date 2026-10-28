using System.ComponentModel;
using Cockpit.App.Docking;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;
using Cockpit.Plugins.Abstractions.Docking;
using NSubstitute;
using Cockpit.Tests.Shared;

namespace Cockpit.App.ViewTests;

// AC-1303: the picked agent takes the middle and the assistant the dock — prototype round 6 had it the other way round.
[Collection("avalonia")]
public sealed class Ac1303ColumnRolesTests
{
    private static IAssistantSettingsStore _SettingsStore()
    {
        var store = Substitute.For<IAssistantSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new AssistantSettings { IsEnabled = true }));
        return store;
    }

    // The parameterless constructor is the previewer's and seeds sample sessions; a real cockpit starts empty.
    private static CockpitViewModel _Cockpit()
    {
        var panels = new DockPanelRegistry();
        panels.Register(new DockPanelRegistration("sample.browser", "Browser", Material.Icons.MaterialIconKind.Web, () => new Avalonia.Controls.TextBlock()));
        var cockpit = new CockpitViewModel(panels) { SimpleView = true };
        cockpit.Sessions.Clear();
        return cockpit;
    }

    // The assistant's session is the host's, and it comes and goes there — switching the assistant off does not
    // touch the stand or the selection, which is exactly what criterion 5 is about.
    private static void _AssistantSession(IAssistantSessionHost host, SessionViewModel? session)
    {
        host.Session.Returns(TestSessions.Assistant(session));
        host.PropertyChanged += Raise.Event<PropertyChangedEventHandler>(
            host, new PropertyChangedEventArgs(nameof(IAssistantSessionHost.Session)));
    }

    private static (CockpitViewModel Cockpit, IAssistantSessionHost Host, SessionViewModel Agent) _Stand(SessionViewModel? assistant)
    {
        var cockpit = _Cockpit();
        var agent = new SessionViewModel { Title = "Kind-cluster als staging-evenbeeld" };
        cockpit.Sessions.Add(agent);

        var host = Substitute.For<IAssistantSessionHost>();
        host.Session.Returns(TestSessions.Assistant(assistant));
        _ = new AssistantChatViewModel(host, _SettingsStore(), Substitute.For<IVoicePlaybackQueue>(), cockpit: cockpit);

        return (cockpit, host, agent);
    }

    // Criterion 5, the gap that actually happened: neither order goes through the rail's click handler a second
    // time. The roles are computed and cannot go stale, so the whole gap is whether the view is *told* —
    // asserting the value alone stayed green with the notification deleted (measured), so this watches the raise.
    [Fact]
    public void SwitchingTheAssistantOffAndOnAgain_TellsTheStandWhereItNowStands()
    {
        var (cockpit, host, agent) = _Stand(assistant: null);
        cockpit.SimpleSelectedSession = agent;

        // Criterion 4(b): with no assistant the picked session takes the column alone, so there is no dock to go back to.
        Assert.False(cockpit.SimpleStandDocksTheAssistant);
        Assert.False(cockpit.SimpleStandShowsTheAssistantInTheMainColumn);

        var announced = new List<string>();
        cockpit.PropertyChanged += (_, e) => announced.Add(e.PropertyName ?? string.Empty);

        // 5(a): the assistant arrives while a session already stands picked.
        _AssistantSession(host, new SessionViewModel { Title = "Assistant" });

        Assert.Contains(nameof(CockpitViewModel.SimpleStandDocksTheAssistant), announced);
        Assert.Contains(nameof(CockpitViewModel.SimpleStandShowsTheAssistantInTheMainColumn), announced);
        Assert.True(cockpit.SimpleStandDocksTheAssistant);

        // AC-1862 (L2a): a plugin panel takes the rail from the docked assistant, and the way back is offered and works.
        Assert.False(cockpit.ShowBackToAssistant);
        cockpit.ToggleDockPanelCommand.Execute("sample.browser");
        Assert.True(cockpit.ShowBackToAssistant);
        cockpit.BackToAssistantCommand.Execute(null);
        Assert.False(cockpit.ShowBackToAssistant);
        Assert.Equal(AssistantIndicatorCoordinator.DockPanelId, cockpit.EffectiveOpenDockPanelId);

        // 5(b): the other order — the agent keeps the column and the dock goes away with the assistant.
        announced.Clear();
        _AssistantSession(host, null);

        Assert.Contains(nameof(CockpitViewModel.SimpleStandDocksTheAssistant), announced);
        Assert.False(cockpit.SimpleStandDocksTheAssistant);
        Assert.False(cockpit.SimpleStandShowsTheAssistantInTheMainColumn);
        Assert.True(agent.IsPaneVisible);
    }
}
