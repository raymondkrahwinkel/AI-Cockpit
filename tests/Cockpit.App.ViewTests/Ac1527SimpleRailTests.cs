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
    // Criterion 1: top to bottom the rail reads New conversation, Home (marked), Projects with its count, the
    // assistant's tree, then Settings, Help and More options — and none of Panels' chrome.
    [Fact]
    public void TheRail_ReadsTopToBottom_WithoutPanelsChrome() => HeadlessAvalonia.Run(() =>
    {
        var (cockpit, _, _) = _Stand();
        cockpit.Projects.StageDesignSample();
        var window = _Shown(cockpit);
        try
        {
            string[] order = ["SimpleRailNewSession", "SimpleRailHome", "SimpleRailProjects", "SimpleRailAssistantRoot",
                "SimpleRailAssistantSessions", "SimpleRailSettings", "SimpleRailHelp", "SimpleRailMoreOptions"];
            var tops = order.Select(name => _Top(window, _Named<Control>(window, name))).ToList();
            Assert.Equal(tops.Order(), tops);

            Assert.True(_Marked(_Named<Button>(window, "SimpleRailHome")));
            Assert.False(_Marked(_Named<Button>(window, "SimpleRailProjects")));
            Assert.Contains(_Named<Button>(window, "SimpleRailProjects").GetVisualDescendants().OfType<TextBlock>(),
                t => t.Text == cockpit.Projects.Projects.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));

            Assert.DoesNotContain(window.GetVisualDescendants().OfType<ViewModeSwitch>(), s => s.IsEffectivelyVisible);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<SidebarFooter>(), f => f.IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    });

    // Criterion 2: Projects shows a card per project and is marked; Open starts a conversation in that project, and
    // that conversation is what the main column then shows.
    [Fact]
    public async Task Projects_ShowsTheCards_AndOpenLandsInTheNewConversation() => await HeadlessAvalonia.RunAsync(async () =>
    {
        var cockpit = await _CockpitThatStartsProjects(Project.Create("Finance") with { DefaultProfileLabel = "work" });
        var window = _Shown(cockpit);
        try
        {
            _Press(_Named<Button>(window, "SimpleRailProjects"));
            window.UpdateLayout();

            var page = _Named<ScrollViewer>(window, "SimpleProjectsPage");
            Assert.True(page.IsEffectivelyVisible);
            Assert.True(_Marked(_Named<Button>(window, "SimpleRailProjects")));
            var card = Assert.Single(page.GetVisualDescendants().OfType<ProjectCardView>());

            var open = card.GetVisualDescendants().OfType<Button>()
                .Single(b => b.IsEffectivelyVisible && b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Start"));
            await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)open.Command!).ExecuteAsync(open.CommandParameter);
            window.UpdateLayout();

            var started = Assert.Single(cockpit.Sessions);
            Assert.Equal("Finance", started.Title);
            Assert.Same(started, cockpit.SimpleSelectedSession);
            Assert.False(page.IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    });

    // Criterion 3: Home brings the start screen back, from a conversation and from Projects alike, and is marked.
    [Fact]
    public void Home_BringsTheStartScreenBack_FromAConversationAndFromProjects() => HeadlessAvalonia.Run(() =>
    {
        var (cockpit, spawned, _) = _Stand();
        var window = _Shown(cockpit);
        try
        {
            var home = _Named<Button>(window, "SimpleRailHome");

            cockpit.SelectSimpleSessionCommand.Execute(spawned);
            _Press(home);
            Assert.True(cockpit.SimpleStandShowsTheStartScreen);
            Assert.Null(cockpit.SimpleSelectedSession);

            _Press(_Named<Button>(window, "SimpleRailProjects"));
            _Press(home);
            window.UpdateLayout();

            Assert.True(cockpit.SimpleStandShowsTheStartScreen);
            Assert.False(_Named<ScrollViewer>(window, "SimpleProjectsPage").IsEffectivelyVisible);
            Assert.True(_Marked(home));
            Assert.False(_Marked(_Named<Button>(window, "SimpleRailProjects")));
        }
        finally
        {
            window.Close();
        }
    });

    // Criterion 4: the assistant's row shows its role and not its profile, and a click puts its conversation in the
    // main column; the rows under it keep their badge and profile.
    [Fact]
    public void TheAssistantsRow_SaysItsRoleNotItsProfile_AndOpensItsConversation() => HeadlessAvalonia.Run(() =>
    {
        var (cockpit, spawned, _) = _Stand();
        var window = _Shown(cockpit);
        try
        {
            var root = _Named<ContentControl>(window, "SimpleRailAssistantRoot");
            var rootTexts = _Texts(root);
            Assert.Contains("Your steady helper", rootTexts);
            Assert.DoesNotContain("assistant-profile", rootTexts);

            var child = _Texts(_Named<ItemsControl>(window, "SimpleRailAssistantSessions"));
            Assert.Contains("CLAUDE", child);
            Assert.Contains("personal", child);

            var row = root.GetVisualDescendants().OfType<Button>().First();
            row.Command!.Execute(row.CommandParameter);

            Assert.Same(cockpit.AssistantRootSession, cockpit.SimpleSelectedSession);
            Assert.True(cockpit.SimpleStandShowsTheAssistantInTheMainColumn);
            Assert.False(cockpit.SimpleStandShowsTheStartScreen);
            Assert.Same(spawned, cockpit.AssistantChat!.SessionsStartedByTheAssistant.Single());
        }
        finally
        {
            window.Close();
        }
    });

    // Criterion 5: Settings is the Options dialog's own command, More options puts the app in Panels, and Panels'
    // switch brings it back to Simple on Home rather than on the conversation left there.
    [Fact]
    public void MoreOptions_GoesToPanels_AndTheSwitchComesBackOnHome() => HeadlessAvalonia.Run(() =>
    {
        var (cockpit, spawned, _) = _Stand();
        var window = _Shown(cockpit);
        try
        {
            Assert.Same(cockpit.OptionsCommand, _Named<Button>(window, "SimpleRailSettings").Command);

            cockpit.SelectSimpleSessionCommand.Execute(spawned);
            _Press(_Named<Button>(window, "SimpleRailMoreOptions"));
            window.UpdateLayout();
            Assert.False(cockpit.SimpleView);

            var simple = window.GetVisualDescendants().OfType<ViewModeSwitch>().Single(s => s.IsEffectivelyVisible)
                .GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Simple"));
            _Press(simple);
            window.UpdateLayout();

            Assert.True(cockpit.SimpleView);
            Assert.True(cockpit.SimpleStandShowsTheStartScreen);
            Assert.True(_Marked(_Named<Button>(window, "SimpleRailHome")));
        }
        finally
        {
            window.Close();
        }
    });

    // AC-1528 criterion 8: a conversation the operator started stands in the rail under its own heading once
    // Home is back, and a click opens it.
    [Fact]
    public void AConversationYouStarted_StandsInTheRail_AndOpensFromThere() => HeadlessAvalonia.Run(() =>
    {
        var (cockpit, _, own) = _Stand();
        var window = _Shown(cockpit);
        try
        {
            cockpit.SelectSimpleSessionCommand.Execute(own);
            _Press(_Named<Button>(window, "SimpleRailHome"));
            window.UpdateLayout();

            Assert.True(_Named<StackPanel>(window, "SimpleRailOwnBranch").IsEffectivelyVisible);
            var row = _Named<ItemsControl>(window, "SimpleRailOwnSessions").GetVisualDescendants().OfType<Button>()
                .Single(b => ReferenceEquals(b.DataContext, own));
            row.Command!.Execute(row.CommandParameter);

            Assert.Same(own, cockpit.SimpleSelectedSession);
        }
        finally
        {
            window.Close();
        }
    });

    // An assistant (on a profile of its own, so criterion 4 can tell it apart), one session it started and one the
    // operator started. The parameterless constructor is the previewer's and seeds sample sessions; those go first.
    private static (CockpitViewModel Cockpit, SessionViewModel Spawned, SessionViewModel Own) _Stand()
    {
        var cockpit = new CockpitViewModel { SimpleView = true };
        cockpit.Sessions.Clear();
        var spawned = _Session("spawned", "Q3 report", startedByTheAssistant: true);
        var own = _Session("own", "Website copy", startedByTheAssistant: false);
        cockpit.Sessions.Add(spawned);
        cockpit.Sessions.Add(own);
        var assistant = new SessionViewModel { Title = "Assistant", ActiveProfileLabel = "assistant-profile" };
        _ = new AssistantChatViewModel(_Host(assistant), _SettingsStore(), Substitute.For<IVoicePlaybackQueue>(), cockpit: cockpit);
        cockpit.ShowSimpleStartScreenCommand.Execute(null);
        return (cockpit, spawned, own);
    }

    private static SessionViewModel _Session(string paneId, string title, bool startedByTheAssistant)
    {
        var session = new SessionViewModel
        {
            Title = title,
            StartedByTheAssistant = startedByTheAssistant,
            ActiveProfileLabel = "personal",
            ProviderBadge = "CLAUDE",
        };
        session.AdoptPaneId(paneId);
        session.ShowPluginHeaderItems = true;
        return session;
    }

    private static IAssistantSessionHost _Host(SessionViewModel session)
    {
        var host = Substitute.For<IAssistantSessionHost>();
        host.Session.Returns(TestSessions.Assistant(session));
        return host;
    }

    private static IAssistantSettingsStore _SettingsStore()
    {
        var store = Substitute.For<IAssistantSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new AssistantSettings { IsEnabled = true }));
        return store;
    }

    // The real quick start behind the card's Start (`ProjectQuickStart` over the real composer), so Open is measured
    // by the session it starts and not by which command it happens to be bound to.
    private static async Task<CockpitViewModel> _CockpitThatStartsProjects(Project project)
    {
        var projectStore = Substitute.For<IProjectStore>();
        projectStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new ProjectSettings { Projects = [project] });
        var dialogs = Substitute.For<ISessionDialogService>();
        var projects = TestProjects.ViewModel(projectStore, dialogs);
        await projects.LoadAsync();

        var profileStore = Substitute.For<ISessionProfileStore>();
        profileStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SessionProfile>>(
            [new SessionProfile("work", new ClaudeConfig("/fake/.claude"))]));
        var mcpCatalog = Substitute.For<IMcpServerCatalog>();
        mcpCatalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<McpServerConfig>>([]));
        var worktrees = Substitute.For<IWorktreeManager>();
        worktrees.ListAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorktreeRecord>());
        var quickStart = new ProjectQuickStart(profileStore, new SessionStartComposer(
            projectStore, mcpCatalog, Substitute.For<ITtySessionProviderResolver>(), new ProjectMemorySourceRegistry(), worktrees));

        var notifications = Substitute.For<INotificationSettingsStore>();
        notifications.LoadAsync().Returns(new NotificationSettings());
        var transcriptDisplay = Substitute.For<ITranscriptDisplaySettingsStore>();
        transcriptDisplay.LoadAsync().Returns(new TranscriptDisplaySettings());
        var sessionBehavior = Substitute.For<ISessionBehaviorSettingsStore>();
        sessionBehavior.LoadAsync().Returns(new SessionBehaviorSettings());
        var layout = Substitute.For<ILayoutSettingsStore>();
        layout.LoadAsync().Returns(new LayoutSettings { OpenInSimpleView = true });
        var voice = Substitute.For<IVoiceSettingsStore>();
        voice.LoadAsync().Returns(new VoiceSettings());
        var terminal = Substitute.For<ITerminalSettingsStore>();
        terminal.LoadAsync().Returns(new TerminalSettings());

        var registry = new SessionRegistry();
        var launcher = new DesktopLauncher.Slot();
        var cockpit = new CockpitViewModel(
            () => new SessionViewModel(),
            () => new TtyViewModel(),
            dialogs,
            Substitute.For<IAudioCaptureService>(),
            Substitute.For<IAudioPlaybackService>(),
            Substitute.For<IAttentionNotifier>(),
            notifications,
            transcriptDisplay,
            sessionBehavior,
            layout,
            voice,
            terminal,
            sessionProfileStore: profileStore,
            worktreeManager: worktrees,
            projects: projects,
            projectQuickStart: quickStart,
            sessionRegistry: registry,
            sessionLauncher: launcher.Get);
        launcher.Launcher = DesktopLauncher.Over(cockpit, registry);
        var desk = Workspace.Create("Sessions", WorkspaceType.Sessions);
        cockpit.Workspaces.Settings = new WorkspaceSettings { Workspaces = [desk], ActiveWorkspaceId = desk.Id };
        return cockpit;
    }

    private static Window _Shown(CockpitViewModel cockpit)
    {
        var window = new Window { Width = 1280, Height = 800, Content = new CockpitView { DataContext = cockpit } };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static T _Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().First(control => control.Name == name);

    private static double _Top(Window window, Control control) => control.TranslatePoint(new Point(0, 0), window)!.Value.Y;

    private static void _Press(Button button) => button.Command!.Execute(button.CommandParameter);

    // The mark the theme draws: `Border.sessionItem.active`, the same one a picked row gets.
    private static bool _Marked(Button entry) =>
        entry.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("sessionItem")).Classes.Contains("active");

    private static List<string?> _Texts(Control control) =>
        control.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
}
