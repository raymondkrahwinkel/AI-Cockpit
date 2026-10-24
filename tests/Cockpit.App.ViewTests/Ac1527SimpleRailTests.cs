using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Assistant;
using Cockpit.Core.Layout;
using Cockpit.Core.Mcp;
using Cockpit.Core.Notifications;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using Cockpit.Core.Workspaces;
using Cockpit.Core.Worktrees;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Tests.Shared;
using NSubstitute;

namespace Cockpit.App.ViewTests;

// AC-1527: the Simple rail as navigation — measured on the whole CockpitView, since the rail's claims are about what is on screen.
[Collection("avalonia")]
public sealed class Ac1527SimpleRailTests
{
    // Criteria 1–3 and AC-1528 #8 as one route: the rail's order without Panels' chrome, Projects, Open into the new
    // conversation, Home back from both pages — and the conversation you started still in the rail, one click away.
    [Fact]
    public async Task TheRail_TakesYouThroughProjectsIntoAConversation_AndHomeAgain() => await HeadlessAvalonia.RunAsync(async () =>
    {
        var (cockpit, window) = await _Shown();
        try
        {
            string[] order = ["SimpleRailNewSession", "SimpleRailHome", "SimpleRailProjects", "SimpleRailAssistantRoot",
                "SimpleRailAssistantSessions", "SimpleRailSettings", "SimpleRailHelp", "SimpleRailMoreOptions"];
            var tops = order.Select(name => _Named<Control>(window, name).TranslatePoint(new Point(0, 0), window)!.Value.Y).ToList();
            Assert.Equal(tops.Order(), tops);
            Assert.Contains("1", _Texts(_Named<Button>(window, "SimpleRailProjects")));
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<ViewModeSwitch>(), s => s.IsEffectivelyVisible);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<SidebarFooter>(), f => f.IsEffectivelyVisible);
            _AssertMarked(window, "SimpleRailHome");

            _Press(window, "SimpleRailProjects");
            _Press(window, "SimpleRailHome");
            _AssertMarked(window, "SimpleRailHome");

            _Press(window, "SimpleRailProjects");
            var page = _Named<ScrollViewer>(window, "SimpleProjectsPage");
            Assert.True(page.IsEffectivelyVisible);
            _AssertMarked(window, "SimpleRailProjects");
            var open = Assert.Single(page.GetVisualDescendants().OfType<ProjectCardView>()).GetVisualDescendants().OfType<Button>()
                .Single(b => b.IsEffectivelyVisible && _Texts(b).Contains("Start"));
            await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)open.Command!).ExecuteAsync(open.CommandParameter);
            window.UpdateLayout();

            var started = cockpit.Sessions.Single(s => s.Title == "Finance");
            Assert.Same(started, cockpit.SimpleSelectedSession);
            Assert.False(page.IsEffectivelyVisible);

            _Press(window, "SimpleRailHome");
            Assert.True(cockpit.SimpleStandShowsTheStartScreen);
            _AssertMarked(window, "SimpleRailHome");

            var row = _Named<ItemsControl>(window, "SimpleRailOwnSessions").GetVisualDescendants().OfType<Button>()
                .Single(b => ReferenceEquals(b.DataContext, started));
            var inPanels = cockpit.SelectedSession;
            row.Command!.Execute(row.CommandParameter);
            Assert.Same(started, cockpit.SimpleSelectedSession);
            Assert.Same(inPanels, cockpit.SelectedSession);
        }
        finally
        {
            window.Close();
        }
    });

    // Criteria 4 and 5: the assistant's row shows its role and not its profile and opens its conversation; Settings
    // is Options; More options goes to Panels, whose switch comes back on Home — the panels pick untouched (AC-1301 #4).
    [Fact]
    public async Task TheAssistantsRow_AndTheWayToPanelsAndBack() => await HeadlessAvalonia.RunAsync(async () =>
    {
        var (cockpit, window) = await _Shown();
        try
        {
            var root = _Named<ContentControl>(window, "SimpleRailAssistantRoot");
            var children = _Texts(_Named<ItemsControl>(window, "SimpleRailAssistantSessions"));
            Assert.Contains("Your steady helper", _Texts(root));
            Assert.DoesNotContain("assistant-profile", _Texts(root));
            Assert.Contains("CLAUDE", children);
            Assert.Contains("personal", children);

            var assistantRow = root.GetVisualDescendants().OfType<Button>().First();
            assistantRow.Command!.Execute(assistantRow.CommandParameter);
            Assert.Same(cockpit.AssistantRootSession, cockpit.SimpleSelectedSession);
            Assert.True(cockpit.SimpleStandShowsTheAssistantInTheMainColumn);
            Assert.False(cockpit.SimpleStandShowsTheStartScreen);

            Assert.Same(cockpit.OptionsCommand, _Named<Button>(window, "SimpleRailSettings").Command);
            var inPanels = cockpit.SelectedSession = cockpit.Sessions.Single(s => s.Title == "Q3 report");
            _Press(window, "SimpleRailMoreOptions");
            Assert.False(cockpit.SimpleView);

            var simple = window.GetVisualDescendants().OfType<ViewModeSwitch>().Single(s => s.IsEffectivelyVisible)
                .GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Simple"));
            simple.Command!.Execute(null);
            window.UpdateLayout();

            Assert.True(cockpit.SimpleStandShowsTheStartScreen);
            Assert.Null(cockpit.SimpleSelectedSession);
            Assert.Same(inPanels, cockpit.SelectedSession);
            _AssertMarked(window, "SimpleRailHome");
        }
        finally
        {
            window.Close();
        }
    });

    // One project behind the real quick start (`ProjectQuickStart` over the real composer, so Open is measured by the
    // session it starts), an assistant on a profile of its own, and one session it started.
    private static async Task<(CockpitViewModel, Window)> _Shown()
    {
        var project = Project.Create("Finance") with { DefaultProfileLabel = "work" };
        var projectStore = Substitute.For<IProjectStore>();
        projectStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new ProjectSettings { Projects = [project] });
        var dialogs = Substitute.For<ISessionDialogService>();
        var projects = TestProjects.ViewModel(projectStore, dialogs);
        await projects.LoadAsync();

        var profiles = Substitute.For<ISessionProfileStore>();
        profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SessionProfile>>(
            [new SessionProfile("work", new ClaudeConfig("/fake/.claude"))]));
        var mcp = Substitute.For<IMcpServerCatalog>();
        mcp.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<McpServerConfig>>([]));
        var worktrees = Substitute.For<IWorktreeManager>();
        worktrees.ListAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorktreeRecord>());
        var quickStart = new ProjectQuickStart(profiles, new SessionStartComposer(
            projectStore, mcp, Substitute.For<ITtySessionProviderResolver>(), new ProjectMemorySourceRegistry(), worktrees));

        var registry = new SessionRegistry();
        var launcher = new DesktopLauncher.Slot();
        var cockpit = new CockpitViewModel(
            () => new SessionViewModel(), () => new TtyViewModel(), dialogs,
            Substitute.For<IAudioCaptureService>(), Substitute.For<IAudioPlaybackService>(), Substitute.For<IAttentionNotifier>(),
            _Store<INotificationSettingsStore, NotificationSettings>(s => s.LoadAsync()),
            _Store<ITranscriptDisplaySettingsStore, TranscriptDisplaySettings>(s => s.LoadAsync()),
            _Store<ISessionBehaviorSettingsStore, SessionBehaviorSettings>(s => s.LoadAsync()),
            _Store<ILayoutSettingsStore, LayoutSettings>(s => s.LoadAsync()),
            _Store<IVoiceSettingsStore, VoiceSettings>(s => s.LoadAsync()),
            _Store<ITerminalSettingsStore, TerminalSettings>(s => s.LoadAsync()),
            sessionProfileStore: profiles, worktreeManager: worktrees, projects: projects, projectQuickStart: quickStart,
            sessionRegistry: registry, sessionLauncher: launcher.Get);
        launcher.Launcher = DesktopLauncher.Over(cockpit, registry);
        var desk = Workspace.Create("Sessions", WorkspaceType.Sessions);
        cockpit.Workspaces.Settings = new WorkspaceSettings { Workspaces = [desk], ActiveWorkspaceId = desk.Id };
        cockpit.SimpleView = true;

        var spawned = new SessionViewModel { Title = "Q3 report", StartedByTheAssistant = true, ActiveProfileLabel = "personal", ProviderBadge = "CLAUDE" };
        spawned.AdoptPaneId("spawned");
        spawned.ShowPluginHeaderItems = true;
        cockpit.Sessions.Add(spawned);
        var host = Substitute.For<IAssistantSessionHost>();
        host.Session.Returns(TestSessions.Assistant(new SessionViewModel { Title = "Assistant", ActiveProfileLabel = "assistant-profile" }));
        var settings = _Store<IAssistantSettingsStore, AssistantSettings>(s => s.LoadAsync(Arg.Any<CancellationToken>()), new() { IsEnabled = true });
        _ = new AssistantChatViewModel(host, settings, Substitute.For<IVoicePlaybackQueue>(), cockpit: cockpit);

        var window = new Window { Width = 1280, Height = 800, Content = new CockpitView { DataContext = cockpit } };
        window.Show();
        window.UpdateLayout();
        return (cockpit, window);
    }

    private static TStore _Store<TStore, TSettings>(Func<TStore, Task<TSettings>> load, TSettings? value = null)
        where TStore : class where TSettings : class, new()
    {
        var store = Substitute.For<TStore>();
        load(store).Returns(value ?? new TSettings());
        return store;
    }

    private static T _Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().First(control => control.Name == name);

    private static void _Press(Window window, string name)
    {
        var button = _Named<Button>(window, name);
        button.Command!.Execute(button.CommandParameter);
        window.UpdateLayout();
    }

    // Exactly one of the two entries carries the mark the theme draws (`Border.sessionItem.active`), and it is this one.
    private static void _AssertMarked(Window window, string name)
    {
        bool Marked(string entry) => _Named<Button>(window, entry).GetVisualDescendants().OfType<Border>()
            .First(b => b.Classes.Contains("sessionItem")).Classes.Contains("active");
        Assert.True(Marked(name));
        Assert.False(Marked(name == "SimpleRailHome" ? "SimpleRailProjects" : "SimpleRailHome"));
    }

    private static List<string?> _Texts(Control control) =>
        control.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
}
