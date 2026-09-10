using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1303: which of the two sessions takes the Simple stand's main column. Picking an agent puts that agent in
/// the middle and the assistant in the dock beside it — the way round that round 6 of the prototype had reversed,
/// where a working panel stood next to a working conversation with the wrong one of the two in the middle.
/// </summary>
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
        var cockpit = new CockpitViewModel { SimpleView = true };
        cockpit.Sessions.Clear();
        return cockpit;
    }

    // The assistant's session is the host's, and it comes and goes there — switching the assistant off does not
    // touch the stand or the selection, which is exactly what criterion 5 is about.
    private static void _AssistantSession(IAssistantSessionHost host, SessionViewModel? session)
    {
        host.Session.Returns(session);
        host.PropertyChanged += Raise.Event<PropertyChangedEventHandler>(
            host, new PropertyChangedEventArgs(nameof(IAssistantSessionHost.Session)));
    }

    private static (CockpitViewModel Cockpit, IAssistantSessionHost Host, SessionViewModel Agent) _Stand(SessionViewModel? assistant)
    {
        var cockpit = _Cockpit();
        var agent = new SessionViewModel { Title = "Kind-cluster als staging-evenbeeld" };
        cockpit.Sessions.Add(agent);

        var host = Substitute.For<IAssistantSessionHost>();
        host.Session.Returns(assistant);
        _ = new AssistantChatViewModel(host, _SettingsStore(), Substitute.For<IVoicePlaybackQueue>(), cockpit: cockpit);

        return (cockpit, host, agent);
    }

    // Criterion 1. Both ways round in one test, because the second is what the first is worth: picking an agent
    // must put the *agent* in the middle, and going back must take the dock away rather than leave it standing empty.
    [Fact]
    public void PickingAnAgent_PutsItInTheMainColumn_AndTheAssistantInTheDock()
    {
        var (cockpit, _, agent) = _Stand(new SessionViewModel { Title = "Assistant" });

        cockpit.SimpleSelectedSession = agent;

        Assert.True(cockpit.SimpleStandDocksTheAssistant);
        Assert.False(cockpit.SimpleStandShowsTheAssistantInTheMainColumn);
        Assert.True(agent.IsPaneVisible);

        cockpit.SimpleSelectedSession = null;

        Assert.False(cockpit.SimpleStandDocksTheAssistant);
        Assert.True(cockpit.SimpleStandShowsTheAssistantInTheMainColumn);
        Assert.False(agent.IsPaneVisible);
    }

    // Criterion 5, the gap that actually happened: neither order goes through the rail's click handler a second
    // time. The roles are computed and cannot go stale, so the whole gap is whether the view is *told* —
    // asserting the value alone stayed green with the notification deleted (measured), so this watches the raise.
    [Fact]
    public void SwitchingTheAssistantOffAndOnAgain_TellsTheStandWhereItNowStands()
    {
        var (cockpit, host, agent) = _Stand(assistant: null);
        cockpit.SimpleSelectedSession = agent;
        Assert.False(cockpit.SimpleStandDocksTheAssistant);

        var announced = new List<string>();
        cockpit.PropertyChanged += (_, e) => announced.Add(e.PropertyName ?? string.Empty);

        // 5(a): the assistant arrives while a session already stands picked.
        _AssistantSession(host, new SessionViewModel { Title = "Assistant" });

        Assert.Contains(nameof(CockpitViewModel.SimpleStandDocksTheAssistant), announced);
        Assert.Contains(nameof(CockpitViewModel.SimpleStandShowsTheAssistantInTheMainColumn), announced);
        Assert.True(cockpit.SimpleStandDocksTheAssistant);

        // 5(b): the other order — the agent keeps the column and the dock goes away with the assistant.
        announced.Clear();
        _AssistantSession(host, null);

        Assert.Contains(nameof(CockpitViewModel.SimpleStandDocksTheAssistant), announced);
        Assert.False(cockpit.SimpleStandDocksTheAssistant);
        Assert.False(cockpit.SimpleStandShowsTheAssistantInTheMainColumn);
        Assert.True(agent.IsPaneVisible);
    }

    // Criterion 3: two composers, each reaching its own session. Built on the `simple-view` scene, which is the
    // docked state — the agent in the main column, the assistant in the rail. The counter-proof this kills is one
    // shared field or one shared send command: both would show up here as a single box, or as two that move together.
    [Fact]
    public void WithTheAssistantDocked_EachColumnHasItsOwnComposer()
    {
        HeadlessAvalonia.Run(() =>
        {
            var window = Screenshotter.ShowScene("simple-view");
            try
            {
                window.UpdateLayout();

                var boxes = window.GetVisualDescendants().OfType<TextBox>()
                    .Where(box => box.Name == "InputBox").ToList();

                Assert.Equal(2, boxes.Count);
                Assert.Single(boxes, box => box.DataContext is AssistantChatViewModel);
                Assert.Single(boxes, box => box.DataContext is SessionViewModel);

                // Typing into one must leave the other alone: a shared field would carry the text across, and a
                // shared send command would need a shared field to read it from.
                var agentBox = boxes.First(box => box.DataContext is SessionViewModel);
                var assistantBox = boxes.First(box => box.DataContext is AssistantChatViewModel);
                agentBox.Text = "restart the kind cluster";

                Assert.Equal("restart the kind cluster", ((SessionViewModel)agentBox.DataContext!).InputText);
                Assert.True(string.IsNullOrEmpty(((AssistantChatViewModel)assistantBox.DataContext!).InputText));
            }
            finally
            {
                window.Close();
            }
        });
    }

    // Criterion 4(b). With nothing to stand beside, the picked session takes the column on its own — and both
    // roles are false at once, which is what says there is no dock and so nothing for a way back to point at.
    [Fact]
    public void WithNoAssistant_ThePickedSessionTakesTheColumnAlone_AndThereIsNoWayBack()
    {
        var (cockpit, _, agent) = _Stand(assistant: null);

        cockpit.SimpleSelectedSession = agent;

        Assert.False(cockpit.SimpleStandDocksTheAssistant);
        Assert.False(cockpit.SimpleStandShowsTheAssistantInTheMainColumn);

        // Picking the assistant's own row is the way back, so with no assistant row there is none to pick: the
        // stand cannot reach a state where something is docked.
        cockpit.SimpleSelectedSession = null;

        Assert.False(cockpit.SimpleStandDocksTheAssistant);
    }
}
