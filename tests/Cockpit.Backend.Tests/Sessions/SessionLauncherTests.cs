using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Projects;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Backend.Tests.Sessions;

// AC-1378: the backend's own `ISessionLauncher`, with a fake runtime and no App: what it starts is in the registry and
// answers a prompt, what it cannot host it refuses with the reason, and a desk closing under a start leaves nothing.
public class SessionLauncherTests
{
    private readonly Workspace _desk = Workspace.Create("Sessions", WorkspaceType.Sessions);
    private readonly SessionRegistry _registry = new();
    private readonly ISessionRuntime _runtime = Substitute.For<ISessionRuntime>();
    private readonly ISessionManager _manager = Substitute.For<ISessionManager>();
    private readonly List<string> _steps = [];

    public SessionLauncherTests()
    {
        _runtime.IsRunning.Returns(true);
        _runtime.StartAsync(
                Arg.Any<SessionProfile?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlySet<string>?>(), Arg.Any<string?>(),
                Arg.Any<SessionResume?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _steps.Add($"start {call.ArgAt<IReadOnlyDictionary<string, string>?>(6)?[Cockpit.Plugins.Abstractions.Sessions.WellKnownPluginSessionOptions.PaneId]}");
                return Task.CompletedTask;
            });
        _manager.Create(Arg.Any<SessionProfile?>()).Returns(_ =>
        {
            _steps.Add("attach");
            return _runtime;
        });
    }

    // Acceptance 1 of AC-1378: red when the session does not appear in `ISessionRegistry.All`.
    [Fact]
    public async Task AnSdkStart_WithoutAnApp_IsInTheRegistry_TakesItsPrompt_AndFormsTheAnswer()
    {
        var started = await _Launcher().StartSessionAsync(_Request(PaneSessionKind.Sdk, prompt: "hello", projectId: "project-1"));

        var handle = Assert.Single(_registry.All);
        // AC-1367: the project rides on the handle, which a project-scoped connect key's visibility is decided on.
        Assert.Equal((started?.PaneId, (bool?)true, "project-1"), (handle.PaneId, started?.PromptDelivered, handle.ProjectId));
        await _runtime.Received(1).SendUserMessageAsync("hello", Arg.Any<IReadOnlyList<ImageAttachment>?>(), Arg.Any<CancellationToken>());

        _Raise(new AssistantTextCompleted { SessionId = "S1", Text = "hello back" }, new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "hello back", IsError = false });

        var transcript = await handle.ReadTranscriptAsync(10);
        Assert.Equal([("UserText", "hello"), ("AssistantText", "hello back")], transcript.Entries.Select(entry => (entry.Kind, entry.Text)));
        Assert.Equal(SessionStatus.Idle, handle.SessionStatus);
    }

    // The same steps the desktop pane runs (`OneStartPathTests`), less the login poll a launcher without a checker skips.
    [Fact]
    public async Task TheBackendStart_RunsTheHostsStartSteps_InOrder()
    {
        var started = await _Launcher().StartSessionAsync(_Request(PaneSessionKind.Sdk));

        Assert.Equal(["attach", $"start {started?.PaneId}", $"timer {SessionHost<QueuedPrompt>.UsageCatchUpInterval}"], _steps);
    }

    private static readonly Dictionary<string, Func<SessionLauncherTests, Task<string?>>> _TtyStarts = new()
    {
        ["kind tty asked for"] = async test =>
            (await Assert.ThrowsAsync<TtyLaunchRefusedException>(() => test._Launcher().StartSessionAsync(test._Request(PaneSessionKind.Tty)))).Message,
        ["the profile's own route"] = async test =>
            (await Assert.ThrowsAsync<TtyLaunchRefusedException>(() => test._Launcher().StartSessionAsync(test._Request(kind: null)))).Message,
        ["start_agent over the launcher"] = async test =>
            (await test._Gateway().SpawnAsync(new AgentSpawnRequest(SpawnTarget.NamedByTheAssistant(test._desk.Id), "work", Kind: "tty"))).Error,
    };

    // Acceptance 3: a TTY session needs a frontend, and the refusal says so, as `WorktreeAdmissionException`'s does.
    // The counter-proof, the desktop starting that profile as a TTY pane, is the App adapter's and stays as it was.
    [Theory]
    [InlineData("kind tty asked for")]
    [InlineData("the profile's own route")]
    [InlineData("start_agent over the launcher")]
    public async Task ATtyStart_IsRefused_WithTheReason_AndStartsNothing(string door)
    {
        var sentence = await _TtyStarts[door](this);

        Assert.Contains("'work' would start as a TTY session, and a TTY session needs a terminal", sentence, StringComparison.Ordinal);
        Assert.Empty(_registry.All);
        _manager.DidNotReceive().Create(Arg.Any<SessionProfile?>());
    }

    // The re-check in the act: a desk that is gone by the time the start registers gets no session, and none runs.
    [Fact]
    public async Task ADeskClosedBeforeTheStartRegisters_GetsNoSession_AndNoneRuns()
    {
        var launcher = _Launcher(Workspace.Create("Other", WorkspaceType.Sessions));
        await launcher.CloseWorkspaceIfEmptyAsync(_desk.Id);

        var started = await launcher.StartSessionAsync(_Request(PaneSessionKind.Sdk));

        Assert.Null(started);
        Assert.Empty(_registry.All);
        _manager.DidNotReceive().Create(Arg.Any<SessionProfile?>());
    }

    // A start that fails halfway leaves no pane behind, and its reason reaches the caller.
    [Fact]
    public async Task AStartThatDoesNotComeUp_LeavesNoSession_AndSaysWhy()
    {
        _runtime.IsRunning.Returns(false);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => _Launcher().StartSessionAsync(_Request(PaneSessionKind.Sdk)));

        Assert.Equal("The provider returned without a running session.", failure.Message);
        Assert.Empty(_registry.All);
        await _manager.Received(1).StopAsync(Arg.Any<string>());
    }

    private SessionLauncher _Launcher(params Workspace[] others) => new(
        new WorkspaceSettings { Workspaces = [_desk, .. others], ActiveWorkspaceId = _desk.Id },
        Substitute.For<IWorkspaceSettingsStore>(),
        Substitute.For<IProjectStore>(),
        _registry,
        _manager,
        new RecordingTime(_steps));

    private SessionLaunchRequest _Request(PaneSessionKind? kind, string? prompt = null, string? projectId = null) => new(
        _desk.Id, new SessionProfile("work", new ClaudeConfig("/fake/.claude")), prompt, WorkingDirectory: null, SessionName: null, kind,
        LaunchOptions: null, IsolateInWorktree: null, ProjectId: projectId, StartedByTheAssistant: false);

    private AssistantAgentGateway _Gateway()
    {
        var profiles = Substitute.For<ISessionProfileStore>();
        profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<SessionProfile>>([new SessionProfile("work", new ClaudeConfig("/fake/.claude"))]));
        return new AssistantAgentGateway(
            _registry,
            _Launcher(),
            Substitute.For<IProjectEditor>(),
            Substitute.For<ISessionWatcher>(),
            Substitute.For<IAssistantConversation>(),
            Substitute.For<IExternalLinkOpener>(),
            profiles,
            Substitute.For<IAssistantSpawnAuditLog>(),
            Substitute.For<IWorkspaceAgentGateway>(),
            new AgentMessageInbox(),
            Substitute.For<IAgentNotifyAuditLog>(),
            Substitute.For<IPluginProviderRegistry>());
    }

    private void _Raise(params SessionEvent[] events) =>
        Array.ForEach(events, evt => _runtime.EventAppended += Raise.Event<Action<SessionEvent>>(evt));

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
