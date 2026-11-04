using Avalonia.Threading;
using Cockpit.Infrastructure.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Agents;
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
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-719 ronde A: an assistant spawn whose folder resolves to a project starts through the same
/// <see cref="ProjectQuickStart"/> door the launcher's Start button and the sidebar's ▶ use, a failed worktree
/// isolation never raises a modal for a caller with no dialog to answer it, and the tri-state <c>isolate</c>
/// override on <c>start_agent</c> behaves exactly as documented — inherit, opt in, or a categorical refusal.
/// </summary>
[Collection("avalonia")]
public class AssistantSpawnProjectIsolationTests
{
    private const string Repository = "/repo";

    [Fact]
    public async Task SpawnWhoseDirectoryResolvesToAProject_InheritsIsolationPromptAndProjectId()
    {
        foreach (var (server, accepted) in new (string?, bool)[]
        {
            (null, true), ("", true), ("cockpit-coding", true), ("cockpit-session", true),
            ("cockpit-elevated", false), ("cockpit-assistant", false), ("unknown", false),
            ("other-project-server", true), ("off-server", false), ("oauth", false), ("project-server", true),
        })
        {
            var profile = new SessionProfile("work", new ClaudeConfig("/fake/.claude"))
            {
                SystemPrompt = "You are Olaf.",
                EnabledMcpServerNames = ["profile-only"],
            };
            var project = Project.Create("Cockpit") with
            {
                SourceDirectories = [new(Repository)],
                IsolateInWorktreeByDefault = true,
                BehaviorPrompt = "Work ticket by ticket.",
                McpOverlay = new ProjectMcpOverlay { EnabledServerNames = ["project-server"] },
            };
            var worktrees = _WorktreeManagerThatIsolatesCleanly();

            var (gateway, cockpit, trail, workspaceId) = Dispatcher.UIThread.Invoke(() => _Gateway(profile, project, worktrees));

            var result = await gateway.SpawnAsync(_Request(workspaceId) with
            {
                WorkingDirectory = Repository,
                ProjectId = project.Id,
                McpServers = server is null ? null : server.Length == 0 ? [] : [server],
            });

            if (!accepted)
            {
                Assert.False(result.Ok, $"MCP server '{server}' must be refused");
                Assert.Contains(server!, result.Error);
                Assert.Equal(result.Error, Assert.Single(trail.Entries).Refusal);
                Assert.Empty(Dispatcher.UIThread.Invoke(() => cockpit.Sessions));
                continue;
            }

            Assert.True(result.Ok, result.Error);
            var launch = Dispatcher.UIThread.Invoke(() => cockpit.Sessions.Single().LaunchResult)!;
            Assert.True(launch.IsolateInWorktree);
            Assert.Contains("Work ticket by ticket.", launch.SystemPrompt);
            Assert.Equal(project.Id, launch.ProjectId);
            if (server is not null)
            {
                Assert.Equal(server.Length == 0 ? [] : new[] { server }, launch.EnabledMcpServerNames);
                Assert.IsType<ExactMcpServerSelection>(launch.EnabledMcpServerNames);
                Assert.Equal(server.Length == 0 ? [] : new[] { server }, Assert.Single(trail.Entries).McpServers);
            }
            else
            {
                Assert.Equal(["project-server"], launch.EnabledMcpServerNames);
                Assert.Null(Assert.Single(trail.Entries).McpServers);
            }
            if (server == "project-server")
            {
                // AC-2083: an explicit list wins over the project, so the server starts without its projectId too.
                var withoutProject = await gateway.SpawnAsync(_Request(workspaceId) with { McpServers = [server] });
                Assert.True(withoutProject.Ok, withoutProject.Error);
            }
        }
    }

    [Fact]
    public async Task ADeskClosedAfterTheSpawnDecided_ButBeforeThePaneLands_IsRefused_AndNoSessionRunsOnIt()
    {
        // AC-1375, the counter-proof of the exclusivity: the gateway decides on the desk in one section and starts after it,
        // and composing the project awaits in between. Without the start's own re-check the pane lands on a desk no
        // tab shows any more — it runs, spends and cannot be reached.
        var profile = new SessionProfile("work", new ClaudeConfig("/fake/.claude"));
        var project = Project.Create("Cockpit") with { SourceDirectories = [new(Repository)] };
        var other = Workspace.Create("Other", WorkspaceType.Sessions);

        var (gateway, cockpit, trail, workspaceId) = Dispatcher.UIThread.Invoke(() => _Gateway(
            profile, project, _WorktreeManagerThatIsolatesCleanly(),
            whileComposing: host => host.Workspaces.Settings = new WorkspaceSettings { Workspaces = [other], ActiveWorkspaceId = other.Id }));

        var result = await gateway.SpawnAsync(_Request(workspaceId) with { WorkingDirectory = Repository });

        Assert.Equal("The cockpit could not start a session just now.", result.Error);
        Assert.Empty(Dispatcher.UIThread.Invoke(() => cockpit.Sessions.ToList()));
        Assert.Equal(result.Error, Assert.Single(trail.Entries).Refusal);
    }

    [Fact]
    public async Task IsolateFalse_IsRefusedOutright_BeforeAnySessionOrWorktreeIsTouched()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/fake/.claude"));
        var project = Project.Create("Cockpit") with { SourceDirectories = [new(Repository)], IsolateInWorktreeByDefault = true };
        var worktrees = _WorktreeManagerThatIsolatesCleanly();

        var (gateway, cockpit, trail, workspaceId) = Dispatcher.UIThread.Invoke(() => _Gateway(profile, project, worktrees));

        var result = await gateway.SpawnAsync(_Request(workspaceId) with { WorkingDirectory = Repository, IsolateInWorktree = false });

        Assert.False(result.Ok);
        Assert.Contains("isolate: false", result.Error);
        Assert.Empty(Dispatcher.UIThread.Invoke(() => cockpit.Sessions));
        Assert.Single(trail.Entries);
        await worktrees.DidNotReceiveWithAnyArgs().CreateForSessionAsync(default!, default, default!);
        await worktrees.DidNotReceiveWithAnyArgs().DetectRepositoryAsync(default!);
    }

    [Fact]
    public async Task FailedIsolation_RefusesWithTheReason_AndNeverShowsAModal()
    {
        // AC-719: a spawn is never interactive — a dialog on the main window would stall a turn the caller cannot
        // see, on a desk the operator may not be looking at. This is the one that used to be reachable only through
        // the operator's own New-session dialog; ronde A is what makes it reachable from a spawn at all.
        var profile = new SessionProfile("work", new ClaudeConfig("/fake/.claude"));
        var project = Project.Create("Cockpit") with { SourceDirectories = [new(Repository)], IsolateInWorktreeByDefault = true };
        var worktrees = Substitute.For<IWorktreeManager>();
        worktrees.ListAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorktreeRecord>());
        worktrees.DetectRepositoryAsync(Repository, Arg.Any<CancellationToken>())
            .Returns(new GitRepositoryInfo(Repository, "abc123", "main"));
        worktrees.CreateForSessionAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<WorktreeSourceHandling>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("fatal: could not create work tree"));

        var dialogs = Substitute.For<ISessionDialogService>();
        var (gateway, cockpit, _, workspaceId) = Dispatcher.UIThread.Invoke(() => _Gateway(profile, project, worktrees, dialogs));

        var result = await gateway.SpawnAsync(_Request(workspaceId) with { WorkingDirectory = Repository });

        Assert.False(result.Ok);
        Assert.Contains("worktree isolation failed", result.Error);
        Assert.Contains("could not create work tree", result.Error);
        Assert.Empty(Dispatcher.UIThread.Invoke(() => cockpit.Sessions));
        await dialogs.DidNotReceiveWithAnyArgs().ShowConfirmationDialogAsync(default!, default!, default!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALiveOrdinarySessionsWorktree_RefusesTheHeadlessSecondWriter(bool sdkProfile)
    {
        var profile = sdkProfile
            ? new SessionProfile("work", new LmStudioConfig("http://localhost:1234", "model"))
            : new SessionProfile("work", new ClaudeConfig("/fake/.claude"));
        var project = Project.Create("Cockpit") with { SourceDirectories = [new(Repository)], IsolateInWorktreeByDefault = true };

        var worktrees = Substitute.For<IWorktreeManager>();
        var (gateway, cockpit, _, workspaceId) = Dispatcher.UIThread.Invoke(() => _Gateway(profile, project, worktrees));

        var owner = Dispatcher.UIThread.Invoke(() =>
        {
            var existing = new SessionViewModel { Title = "Still running" };
            cockpit.Sessions.Add(existing);
            return existing.PaneId;
        });
        worktrees.ListAsync(Arg.Any<CancellationToken>()).Returns([
            new WorktreeRecord(owner, Repository, Repository, "someone-elses-branch", "abc123", DateTimeOffset.UnixEpoch),
        ]);
        worktrees.ReattachAsync(Repository, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new WorktreeAdmissionException(Repository, owner));

        var result = await gateway.SpawnAsync(_Request(workspaceId) with { WorkingDirectory = Repository });

        Assert.False(result.Ok);
        Assert.Contains($"Cannot start in '{Repository}'", result.Error);
        Assert.Contains($"live Cockpit session '{owner}'", result.Error);
        Assert.Contains("already owns that worktree", result.Error);
        await worktrees.Received(1).ReattachAsync(Repository, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await worktrees.DidNotReceiveWithAnyArgs().CreateForSessionAsync(default!, default, default!);
    }

    [Fact]
    public async Task SpawnWithAnUnknownProjectId_IsRefused_AndNeverFallsBackToTheFolder()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/fake/.claude"));
        var project = Project.Create("Cockpit") with { SourceDirectories = [new(Repository)], DefaultProfileLabel = "work" };
        var worktrees = _WorktreeManagerThatIsolatesCleanly();

        var (gateway, cockpit, trail, workspaceId) = Dispatcher.UIThread.Invoke(() => _Gateway(profile, project, worktrees));

        // Repository would map-match the real project if resolution ever fell through to that — it must not.
        var result = await gateway.SpawnAsync(new AgentSpawnRequest(
            SpawnTarget.NamedByTheAssistant(workspaceId), ProfileLabel: null, ProjectId: "does-not-exist", WorkingDirectory: Repository));

        Assert.False(result.Ok);
        Assert.Contains("no project with id 'does-not-exist'", result.Error);
        Assert.Empty(Dispatcher.UIThread.Invoke(() => cockpit.Sessions));
        Assert.Single(trail.Entries);
    }

    private static IWorktreeManager _WorktreeManagerThatIsolatesCleanly()
    {
        var worktrees = Substitute.For<IWorktreeManager>();
        worktrees.ListAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorktreeRecord>());
        worktrees.DetectRepositoryAsync(Repository, Arg.Any<CancellationToken>())
            .Returns(new GitRepositoryInfo(Repository, "abc123", "main"));
        worktrees.CreateForSessionAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<WorktreeSourceHandling>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new WorktreeRecord("owner", Repository, "/repo-wt", "ac-719-branch", "abc123", DateTimeOffset.UnixEpoch)));
        return worktrees;
    }

    private static AgentSpawnRequest _Request(string workspaceId) =>
        new(SpawnTarget.NamedByTheAssistant(workspaceId), "work");

    private static (AssistantAgentGateway Gateway, CockpitViewModel Cockpit, RecordingSpawnTrail Trail, string WorkspaceId) _Gateway(
        SessionProfile profile, Project project, IWorktreeManager worktreeManager, ISessionDialogService? dialogService = null,
        LiveSessionRegistry? liveSessions = null,
        SessionRegistry? sessionRegistry = null,
        Action<CockpitViewModel>? whileComposing = null)
    {
        var sessions = sessionRegistry ?? new SessionRegistry();
        var desk = Workspace.Create("Release", WorkspaceType.Sessions);
        var settings = new WorkspaceSettings { Workspaces = [desk], ActiveWorkspaceId = desk.Id };

        var projectStore = Substitute.For<IProjectStore>();
        projectStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new ProjectSettings { Projects = [project] });
        var dialogs = dialogService ?? Substitute.For<ISessionDialogService>();
        var projects = TestProjects.ViewModel(projectStore, dialogs);
        projects.LoadAsync().GetAwaiter().GetResult();

        var profileStore = Substitute.For<ISessionProfileStore>();
        profileStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SessionProfile>>([profile]));
        var mcpCatalog = Substitute.For<IMcpServerCatalog>();
        CockpitViewModel? composingFor = null;
        mcpCatalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            whileComposing?.Invoke(composingFor ?? throw new InvalidOperationException("Composed before the cockpit was built."));
            return Task.FromResult<IReadOnlyList<McpServerConfig>>([
                new() { Name = "cockpit-coding" },
                new() { Name = "cockpit-session", AlwaysMounted = true },
                new() { Name = "cockpit-elevated", Internal = true },
                new() { Name = "cockpit-assistant", Internal = true },
                new() { Name = "off-server", Enabled = false },
                new() { Name = "oauth", Auth = McpServerAuth.OAuth },
                .. call.ArgAt<string?>(0) == project.Id ? new McpServerConfig[] { new() { Name = "project-server", ProjectLinked = true } } : [],
            ]);
        });
        mcpCatalog.GetServersAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<McpServerConfig>>([
            new() { Name = "other-project-server", ProjectLinked = true },
            new() { Name = "project-server", ProjectLinked = true },
        ]));
        var quickStart = new ProjectQuickStart(profileStore, new SessionStartComposer(
            projectStore, mcpCatalog, Substitute.For<ITtySessionProviderResolver>(), new ProjectMemorySourceRegistry(), worktreeManager));

        var notificationSettingsStore = Substitute.For<INotificationSettingsStore>();
        notificationSettingsStore.LoadAsync().Returns(new NotificationSettings());
        var transcriptDisplaySettingsStore = Substitute.For<ITranscriptDisplaySettingsStore>();
        transcriptDisplaySettingsStore.LoadAsync().Returns(new TranscriptDisplaySettings());
        var sessionBehaviorSettingsStore = Substitute.For<ISessionBehaviorSettingsStore>();
        sessionBehaviorSettingsStore.LoadAsync().Returns(new SessionBehaviorSettings());
        var layoutSettingsStore = Substitute.For<ILayoutSettingsStore>();
        layoutSettingsStore.LoadAsync().Returns(new LayoutSettings());
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync().Returns(new VoiceSettings());
        var terminalSettingsStore = Substitute.For<ITerminalSettingsStore>();
        terminalSettingsStore.LoadAsync().Returns(new TerminalSettings());

        var cockpit = new CockpitViewModel(
            () => new SessionViewModel(),
            () => new TtyViewModel(),
            dialogs,
            Substitute.For<IAudioCaptureService>(),
            Substitute.For<IAudioPlaybackService>(),
            Substitute.For<IAttentionNotifier>(),
            notificationSettingsStore,
            transcriptDisplaySettingsStore,
            sessionBehaviorSettingsStore,
            layoutSettingsStore,
            voiceSettingsStore,
            terminalSettingsStore,
            sessionProfileStore: profileStore,
            worktreeManager: worktreeManager,
            projects: projects,
            projectQuickStart: quickStart,
            liveSessions: liveSessions,
            sessionRegistry: sessions);
        cockpit.Workspaces.Settings = settings;
        composingFor = cockpit;

        var trail = new RecordingSpawnTrail();
        return (
            AssistantAgentGatewayGraph.Over(
                cockpit,
                sessions,
                profileStore,
                trail,
                Substitute.For<IWorkspaceAgentGateway>(),
                Substitute.For<IAgentMessageInbox>(),
                Substitute.For<IAgentNotifyAuditLog>(),
                Substitute.For<IPluginProviderRegistry>(),
                new SessionWatcher(Substitute.For<IAgentMessageInbox>()),
                Substitute.For<IAssistantSessionHost>(),
                worktreeManager,
                mcpServerCatalog: mcpCatalog),
            cockpit,
            trail,
            desk.Id);
    }

    private sealed class RecordingSpawnTrail : IAssistantSpawnAuditLog
    {
        public List<AssistantSpawnAuditEntry> Entries { get; } = [];

        public Task RecordAsync(AssistantSpawnAuditEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AssistantSpawnAuditEntry>> ReadRecentAsync(int limit = 200, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AssistantSpawnAuditEntry>>([.. Enumerable.Reverse(Entries)]);
    }
}
