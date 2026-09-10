using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1302: the Simple stand's rail — the assistant as the root session, the sessions it started under it.
/// The failure this guards against looks right: a rail fed from <c>LiveSessions</c> is full, reacts, and is
/// wrong, because it hangs sessions under an assistant that never started them.
/// </summary>
[Collection("avalonia")]
public sealed class Ac1302SimpleRailTests
{
    private static IAssistantSessionHost _Host(SessionViewModel? session)
    {
        var host = Substitute.For<IAssistantSessionHost>();
        host.Session.Returns(session);
        return host;
    }

    private static IAssistantSettingsStore _SettingsStore()
    {
        var store = Substitute.For<IAssistantSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new AssistantSettings { IsEnabled = true }));
        return store;
    }

    private static AssistantChatViewModel _Chat(CockpitViewModel cockpit, SessionViewModel? assistant) =>
        new(_Host(assistant), _SettingsStore(), Substitute.For<IVoicePlaybackQueue>(), cockpit: cockpit);

    // The parameterless constructor is the previewer's and seeds sample sessions; a real cockpit starts empty.
    private static CockpitViewModel _Cockpit()
    {
        var cockpit = new CockpitViewModel { SimpleView = true };
        cockpit.Sessions.Clear();
        return cockpit;
    }

    private static SessionViewModel _Session(string paneId, string title, bool startedByTheAssistant)
    {
        var session = new SessionViewModel { Title = title, StartedByTheAssistant = startedByTheAssistant };
        session.AdoptPaneId(paneId);
        session.ShowPluginHeaderItems = true;
        return session;
    }

    private static Window _Shown(CockpitViewModel cockpit)
    {
        var window = new Window { Width = 1100, Height = 760, Content = new CockpitView { DataContext = cockpit } };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static T _Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().First(control => control.Name == name);

    // Criterion 1: the tree draws the relation, not what is alive. Both sessions live and neither is placed on a
    // desk of its own, so only the stamp tells them apart — and only the one the assistant started hangs under it.
    [Fact]
    public void TheAssistantsBranch_HoldsOnlyTheSessionsItStarted()
    {
        HeadlessAvalonia.Run(() =>
        {
            var cockpit = _Cockpit();
            cockpit.Sessions.Add(_Session("spawned", "spawned", startedByTheAssistant: true));
            cockpit.Sessions.Add(_Session("own", "opened by the operator", startedByTheAssistant: false));
            _ = _Chat(cockpit, new SessionViewModel { Title = "Assistant" });

            var window = _Shown(cockpit);
            try
            {
                var branch = _Named<ItemsControl>(window, "SimpleRailAssistantSessions");

                Assert.Equal(
                    ["spawned"],
                    branch.ItemsSource!.Cast<SessionPanelViewModel>().Select(session => session.Title));
            }
            finally
            {
                window.Close();
            }
        });
    }

    // Criterion 4: an assistant session is what there is a root to draw. Without one the rail lists the sessions
    // flat rather than drawing an empty "Assistant" node; with one the tree stands and the flat list is away.
    [Fact]
    public void WithoutAnAssistantSession_TheRailListsTheSessionsFlat()
    {
        HeadlessAvalonia.Run(() =>
        {
            var cockpit = _Cockpit();
            cockpit.Sessions.Add(_Session("spawned", "spawned", startedByTheAssistant: true));
            _ = _Chat(cockpit, assistant: null);

            var window = _Shown(cockpit);
            try
            {
                Assert.False(_Named<StackPanel>(window, "SimpleRailAssistantBranch").IsVisible);
                Assert.True(_Named<StackPanel>(window, "SimpleRailFlatBranch").IsVisible);

                var flat = _Named<ItemsControl>(window, "SimpleRailFlatSessions");
                Assert.Equal(
                    ["spawned"],
                    flat.ItemsSource!.Cast<SessionPanelViewModel>().Select(session => session.Title));

                // The other half: an assistant arriving swaps the rail over with nothing rebuilding it.
                cockpit.AssistantChat = _Chat(cockpit, new SessionViewModel { Title = "Assistant" });
                window.UpdateLayout();

                Assert.True(_Named<StackPanel>(window, "SimpleRailAssistantBranch").IsVisible);
                Assert.False(_Named<StackPanel>(window, "SimpleRailFlatBranch").IsVisible);
                Assert.Same(
                    cockpit.AssistantChat.Session,
                    _Named<ContentControl>(window, "SimpleRailAssistantRoot").Content);
            }
            finally
            {
                window.Close();
            }
        });
    }

    // Criterion 2: what a row says is what the session says. A statusline the agent sets after the row is drawn
    // appears without anything reading it again, and a session with none has no line rather than a blank one.
    [Fact]
    public void AStatuslineArrivingLater_ShowsUp_AndAnEmptyOneDrawsNoLine()
    {
        HeadlessAvalonia.Run(() =>
        {
            var cockpit = _Cockpit();
            var session = _Session("spawned", "spawned", startedByTheAssistant: true);
            cockpit.Sessions.Add(session);
            _ = _Chat(cockpit, new SessionViewModel { Title = "Assistant" });

            var window = _Shown(cockpit);
            try
            {
                var line = window.GetVisualDescendants().OfType<TextBlock>()
                    .First(block => block.Name == "SimpleRailStatusline"
                        && ReferenceEquals(block.DataContext, session));

                Assert.False(line.IsVisible);

                session.Statusline = "kind→staging gelijktrekken";
                window.UpdateLayout();

                Assert.True(line.IsVisible);
                Assert.Equal("kind→staging gelijktrekken", line.Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    // AC-1301 left `SimpleSelectedSession` without a producer — this is it. Clicking a node picks that session,
    // and only in this stand: the panels stand keeps the selection it had.
    [Fact]
    public void ClickingANode_PicksThatSessionForTheSimpleStandOnly()
    {
        HeadlessAvalonia.Run(() =>
        {
            var cockpit = _Cockpit();
            var spawned = _Session("spawned", "spawned", startedByTheAssistant: true);
            cockpit.Sessions.Add(spawned);
            _ = _Chat(cockpit, new SessionViewModel { Title = "Assistant" });

            var window = _Shown(cockpit);
            try
            {
                var row = window.GetVisualDescendants().OfType<Button>()
                    .First(button => ReferenceEquals(button.DataContext, spawned));
                var inPanels = cockpit.SelectedSession;

                row.Command!.Execute(row.CommandParameter);

                Assert.Same(spawned, cockpit.SimpleSelectedSession);
                Assert.Same(inPanels, cockpit.SelectedSession);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
