using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.Profiles;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Sessions;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.App.ViewTests;

// AC-1378 (acceptance 2): the operator's own start and an agent's `start_agent` are one start path. Each lands exactly
// one pane, and each reaches the runtime through `SessionHost.StartAsync`'s steps in the order the pane always ran them.
[Collection("avalonia")]
public class OneStartPathTests
{
    private static readonly Dictionary<string, Func<Graph, Task>> _Doors = new()
    {
        ["new-session dialog"] = graph => Dispatcher.UIThread.InvokeAsync(() => graph.Cockpit.NewSessionCommand.ExecuteAsync(null)),
        ["start_agent"] = graph => graph.Gateway.SpawnAsync(new AgentSpawnRequest(SpawnTarget.NamedByTheAssistant(graph.DeskId), "work", Kind: "sdk")),
    };

    // Red when a door makes two panes or none, and when a step of the host's start goes missing or moves on either door.
    [Theory]
    [InlineData("new-session dialog")]
    [InlineData("start_agent")]
    public async Task EachStartDoor_LandsOnePane_ThroughTheHostsStartSteps_InOrder(string door)
    {
        var graph = Dispatcher.UIThread.Invoke(_Graph);

        await _Doors[door](graph);

        var pane = Assert.IsType<SessionViewModel>(Dispatcher.UIThread.Invoke(() => Assert.Single(graph.Cockpit.Sessions)));
        Assert.Equal(
            [
                // The login poll's timer, its first check, the runtime, its start with the pane id, the usage catch-up.
                $"timer {Timeout.InfiniteTimeSpan}",
                "login",
                "attach",
                $"start {pane.PaneId}",
                $"timer {SessionHost<QueuedPrompt>.UsageCatchUpInterval}",
            ],
            graph.Steps);
    }

    private sealed record Graph(CockpitViewModel Cockpit, AssistantAgentGateway Gateway, string DeskId, List<string> Steps);

    private static Graph _Graph()
    {
        var steps = new List<string>();
        var runtime = Substitute.For<ISessionRuntime>();
        runtime.IsRunning.Returns(true);
        runtime.StartAsync(
                Arg.Any<SessionProfile?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlySet<string>?>(), Arg.Any<string?>(),
                Arg.Any<SessionResume?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                steps.Add($"start {call.ArgAt<IReadOnlyDictionary<string, string>?>(6)?[WellKnownPluginSessionOptions.PaneId]}");
                return Task.CompletedTask;
            });
        var manager = Substitute.For<ISessionManager>();
        manager.Create(Arg.Any<SessionProfile?>()).Returns(_ =>
        {
            steps.Add("attach");
            return runtime;
        });
        var loginChecker = Substitute.For<IProfileLoginChecker>();
        loginChecker.IsLoggedIn(Arg.Any<SessionProfile>()).Returns(_ =>
        {
            steps.Add("login");
            return true;
        });
        var time = new RecordingTime(steps);

        var profile = new SessionProfile("work", new ClaudeConfig(@"C:\fake\.claude")) { DefaultKind = ProfileSessionKind.Sdk };
        var dialogs = Substitute.For<ISessionDialogService>();
        dialogs.ShowNewSessionDialogAsync(Arg.Any<NewSessionPrefill?>(), Arg.Any<bool>(), Arg.Any<Cockpit.Core.Projects.Project?>())
            .Returns(new NewSessionResult(
                SessionKind.Sdk, profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel,
                SessionOptionCatalog.DefaultEffort, "one"));

        var registry = new SessionRegistry();
        var cockpit = _Cockpit(() => new SessionViewModel(manager, loginChecker: loginChecker, timeProvider: time), dialogs, registry);
        var desk = Workspace.Create("Sessions", WorkspaceType.Sessions);
        cockpit.Workspaces.Settings = new WorkspaceSettings { Workspaces = [desk], ActiveWorkspaceId = desk.Id };

        var profiles = Substitute.For<ISessionProfileStore>();
        profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SessionProfile>>([profile]));
        var gateway = AssistantAgentGatewayGraph.Over(
            cockpit,
            registry,
            profiles,
            Substitute.For<IAssistantSpawnAuditLog>(),
            Substitute.For<IWorkspaceAgentGateway>(),
            Substitute.For<IAgentMessageInbox>(),
            Substitute.For<IAgentNotifyAuditLog>(),
            Substitute.For<IPluginProviderRegistry>(),
            new SessionWatcher(Substitute.For<IAgentMessageInbox>()),
            Substitute.For<IAssistantSessionHost>());

        return new Graph(cockpit, gateway, desk.Id, steps);
    }

    private static CockpitViewModel _Cockpit(Func<SessionViewModel> sessions, ISessionDialogService dialogs, SessionRegistry registry)
    {
        var notifications = Substitute.For<INotificationSettingsStore>();
        notifications.LoadAsync().Returns(new NotificationSettings());
        var transcriptDisplay = Substitute.For<ITranscriptDisplaySettingsStore>();
        transcriptDisplay.LoadAsync().Returns(new TranscriptDisplaySettings());
        var sessionBehavior = Substitute.For<ISessionBehaviorSettingsStore>();
        sessionBehavior.LoadAsync().Returns(new SessionBehaviorSettings());
        var layout = Substitute.For<ILayoutSettingsStore>();
        layout.LoadAsync().Returns(new LayoutSettings());
        var voice = Substitute.For<IVoiceSettingsStore>();
        voice.LoadAsync().Returns(new VoiceSettings());
        var terminal = Substitute.For<ITerminalSettingsStore>();
        terminal.LoadAsync().Returns(new TerminalSettings());

        return new CockpitViewModel(
            sessions,
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
            sessionRegistry: registry);
    }

    // Every timer the host asks for, by its period; none of them ever ticks.
    private sealed class RecordingTime(List<string> steps) : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            steps.Add($"timer {period}");
            return Substitute.For<ITimer>();
        }
    }
}
