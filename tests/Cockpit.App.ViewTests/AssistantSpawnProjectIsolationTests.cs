using Avalonia.Threading;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
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
using Cockpit.Core.Notifications;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using Cockpit.Core.Workspaces;
using Cockpit.Core.Worktrees;
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
        var profile = new SessionProfile("work", new ClaudeConfig("/fake/.claude")) { SystemPrompt = "You are Olaf." };
        var project = Project.Create("Cockpit") with
        {
            SourceDirectories = [new(Repository)],
            IsolateInWorktreeByDefault = true,
            BehaviorPrompt = "Work ticket by ticket.",
        };
        var worktrees = _WorktreeManagerThatIsolatesCleanly();

        var (gateway, cockpit, _, workspaceId) = Dispatcher.UIThread.Invoke(() => _Gateway(profile, project, worktrees));

        var result = await gateway.SpawnAsync(_Request(workspaceId) with { WorkingDirectory = Repository });

        Assert.True(result.Ok, result.Error);
        var launch = Dispatcher.UIThread.Invoke(() => cockpit.Sessions.Single().LaunchResult)!;
        Assert.True(launch.IsolateInWorktree);
        Assert.Contains("Work ticket by ticket.", launch.SystemPrompt);
        Assert.Equal(project.Id, launch.ProjectId);
    }

    [Fact]
    public async Task IsolationOmitted_WithAProjectThatAsksForNone_StartsUnisolated()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/fake/.claude"));
        var project = Project.Create("Cockpit") with { SourceDirectories = [new(Repository)], IsolateInWorktreeByDefault = false };
        var worktrees = _WorktreeManagerThatIsolatesCleanly();

        var (gateway, cockpit, _, workspaceId) = Dispatcher.UIThread.Invoke(() => _Gateway(profile, project, worktrees));

        var result = await gateway.SpawnAsync(_Request(workspaceId) with { WorkingDirectory = Repository });

        Assert.True(result.Ok, result.Error);
        var launch = Dispatcher.UIThread.Invoke(() => cockpit.Sessions.Single().LaunchResult)!;
        Assert.False(launch.IsolateInWorktree);
        await worktrees.DidNotReceiveWithAnyArgs().CreateForSessionAsync(default!, default, default!);
    }

    [Fact]
    public async Task IsolateTrue_OverridesAProjectThatAsksForNone()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/fake/.claude"));
        var project = Project.Create("Cockpit") with { SourceDirectories = [new(Repository)], IsolateInWorktreeByDefault = false };
        var worktrees = _WorktreeManagerThatIsolatesCleanly();

        var (gateway, cockpit, _, workspaceId) = Dispatcher.UIThread.Invoke(() => _Gateway(profile, project, worktrees));

        var result = await gateway.SpawnAsync(_Request(workspaceId) with { WorkingDirectory = Repository, IsolateInWorktree = true });

        Assert.True(result.Ok, result.Error);
        var launch = Dispatcher.UIThread.Invoke(() => cockpit.Sessions.Single().LaunchResult)!;
        Assert.True(launch.IsolateInWorktree);
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
        worktrees.CreateForSessionAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<WorktreeSourceHandling>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
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

    [Fact]
    public async Task ALiveOrdinarySessionsWorktree_RefusesTheHeadlessSecondWriter()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/fake/.claude"));
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

        var result = await gateway.SpawnAsync(_Request(workspaceId) with { WorkingDirectory = Repository });

        Assert.False(result.Ok);
        Assert.Contains("another live Cockpit session", result.Error);
        await worktrees.Received(1).ReattachAsync(Repository, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await worktrees.DidNotReceiveWithAnyArgs().CreateForSessionAsync(default!, default, default!);
    }

    [Fact]
    public async Task ALiveOrdinarySessionsWorktree_RefusesTheHeadlessSdkSecondWriter()
    {
        var profile = new SessionProfile("work", new LmStudioConfig("http://localhost:1234", "model"));
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

        var result = await gateway.SpawnAsync(_Request(workspaceId) with { WorkingDirectory = Repository });

        Assert.False(result.Ok);
        Assert.Contains("another live Cockpit session", result.Error);
        await worktrees.Received(1).ReattachAsync(Repository, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssistantOwnedWorktree_IsReattachedToTheSpawnedSession()
    {
        // AC-719 ronde B: a worktree the assistant made for itself must hand over to the session started in it,
        // or it stays stuck on the assistant forever (always "live" by construction). A real LiveSessionRegistry
        // is wired here, not the Sessions fallback the other tests rely on, so that rule is actually exercised.
        var profile = new SessionProfile("work", new ClaudeConfig("/fake/.claude"));
        var project = Project.Create("Cockpit") with { SourceDirectories = [new(Repository)], IsolateInWorktreeByDefault = true };

        const string AssistantMadeWorktree = "/repo-wt";
        var worktrees = Substitute.For<IWorktreeManager>();
        var (gateway, cockpit, _, workspaceId) = Dispatcher.UIThread.Invoke(() =>
            _Gateway(profile, project, worktrees, liveSessions: new LiveSessionRegistry([])));

        // Path distinct from the repository itself — a real worktree never shares the source checkout's folder —
        // so EmbeddedSessionProject.Resolve can trace it back to the project through its RepositoryRoot, the same
        // way the assistant would have named this folder as workingDirectory after making it with worktree_create.
        var record = new WorktreeRecord(AssistantIdentity.PaneId, Repository, AssistantMadeWorktree, "assistant-made-branch", "abc123", DateTimeOffset.UnixEpoch);
        worktrees.ListAsync(Arg.Any<CancellationToken>()).Returns([record]);
        worktrees.ReattachAsync(AssistantMadeWorktree, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult<WorktreeRecord?>(record with { SessionId = callInfo.ArgAt<string>(1) }));

        var result = await gateway.SpawnAsync(_Request(workspaceId) with { WorkingDirectory = AssistantMadeWorktree });

        Assert.True(result.Ok, result.Error);
        var pane = Dispatcher.UIThread.Invoke(() => cockpit.Sessions.Single());
        await worktrees.Received(1).ReattachAsync(AssistantMadeWorktree, pane.PaneId, Arg.Any<CancellationToken>());
        await worktrees.DidNotReceiveWithAnyArgs().CreateForSessionAsync(default!, default, default!);
    }

    // --- AC-773: projectId as its own way to find the project, alongside the folder map-match above ------------

    [Fact]
    public async Task SpawnWithProjectId_NoProfileOrWorkingDirectory_UsesTheProjectsDefaultsAndReportsTheResolvedProfile()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/fake/.claude"));
        var project = Project.Create("Cockpit") with
        {
            SourceDirectories = [new(Repository)],
            DefaultProfileLabel = "work",
            IsolateInWorktreeByDefault = true,
            BehaviorPrompt = "Work ticket by ticket.",
        };
        var worktrees = _WorktreeManagerThatIsolatesCleanly();

        var (gateway, cockpit, trail, workspaceId) = Dispatcher.UIThread.Invoke(() => _Gateway(profile, project, worktrees));

        var result = await gateway.SpawnAsync(
            new AgentSpawnRequest(SpawnTarget.NamedByTheAssistant(workspaceId), ProfileLabel: null, ProjectId: project.Id));

        Assert.True(result.Ok, result.Error);
        Assert.Equal("work", result.ResolvedProfileLabel);
        var launch = Dispatcher.UIThread.Invoke(() => cockpit.Sessions.Single().LaunchResult)!;
        Assert.True(launch.IsolateInWorktree);
        Assert.Equal(Repository, launch.WorkingDirectory);
        Assert.Contains("Work ticket by ticket.", launch.SystemPrompt);
        Assert.Equal(project.Id, launch.ProjectId);
        Assert.Equal(project.Id, trail.Entries.Single().ProjectId);
    }

    [Fact]
    public async Task SpawnWithProjectId_OnAnExternalFolderExplicitlyGiven_StillIsolatesViaTheId_ButKeepsTheExplicitFolder()
    {
        // AC-682's restgat, closed by AC-773: a folder outside any project's own directory never map-matches
        // (ProjectDirectoryMatch finds nothing there), but an explicit projectId still finds the project and applies
        // its isolation default — while the explicit working directory itself is never overridden by the project's.
        const string ExternalFolder = "/elsewhere";
        var profile = new SessionProfile("work", new ClaudeConfig("/fake/.claude"));
        var project = Project.Create("Cockpit") with
        {
            SourceDirectories = [new(Repository)],
            DefaultProfileLabel = "work",
            IsolateInWorktreeByDefault = true,
        };
        var worktrees = _WorktreeManagerThatIsolatesCleanly();
        worktrees.DetectRepositoryAsync(ExternalFolder, Arg.Any<CancellationToken>())
            .Returns(new GitRepositoryInfo(ExternalFolder, "def456", "main"));

        var (gateway, cockpit, _, workspaceId) = Dispatcher.UIThread.Invoke(() => _Gateway(profile, project, worktrees));

        var result = await gateway.SpawnAsync(new AgentSpawnRequest(
            SpawnTarget.NamedByTheAssistant(workspaceId), ProfileLabel: null, ProjectId: project.Id, WorkingDirectory: ExternalFolder));

        Assert.True(result.Ok, result.Error);
        var launch = Dispatcher.UIThread.Invoke(() => cockpit.Sessions.Single().LaunchResult)!;
        Assert.True(launch.IsolateInWorktree);
        Assert.Equal(ExternalFolder, launch.WorkingDirectory);
    }

    [Fact]
    public async Task SpawnWithProjectId_AndAnExplicitProfile_NeverFallsBackToTheProjectsDefault()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/fake/.claude"));
        // Names a profile that does not exist: if the explicit label did not win outright, resolution would fall
        // back to it and this spawn would be refused with "no profile called 'slow'" instead of succeeding.
        var project = Project.Create("Cockpit") with { SourceDirectories = [new(Repository)], DefaultProfileLabel = "slow" };
        var worktrees = _WorktreeManagerThatIsolatesCleanly();

        var (gateway, _, _, workspaceId) = Dispatcher.UIThread.Invoke(() => _Gateway(profile, project, worktrees));

        var result = await gateway.SpawnAsync(new AgentSpawnRequest(
            SpawnTarget.NamedByTheAssistant(workspaceId), ProfileLabel: "work", ProjectId: project.Id));

        Assert.True(result.Ok, result.Error);
        Assert.Equal("work", result.ResolvedProfileLabel);
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

    [Fact]
    public async Task SpawnWithNoProjectIdAndNoProfile_IsRefused_JustLikeMissingProfileAlwaysWas()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/fake/.claude"));
        var project = Project.Create("Cockpit") with { SourceDirectories = [new(Repository)], DefaultProfileLabel = "work" };
        var worktrees = _WorktreeManagerThatIsolatesCleanly();

        var (gateway, cockpit, _, workspaceId) = Dispatcher.UIThread.Invoke(() => _Gateway(profile, project, worktrees));

        var result = await gateway.SpawnAsync(new AgentSpawnRequest(SpawnTarget.NamedByTheAssistant(workspaceId), ProfileLabel: null));

        Assert.False(result.Ok);
        Assert.Contains("A profile is required", result.Error);
        Assert.Empty(Dispatcher.UIThread.Invoke(() => cockpit.Sessions));
    }

    [Fact]
    public async Task SpawnWithProjectId_WhoseProjectHasNoDefaultProfile_AndNoExplicitProfile_IsRefused()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/fake/.claude"));
        var project = Project.Create("Cockpit") with { SourceDirectories = [new(Repository)], DefaultProfileLabel = null };
        var worktrees = _WorktreeManagerThatIsolatesCleanly();

        var (gateway, cockpit, _, workspaceId) = Dispatcher.UIThread.Invoke(() => _Gateway(profile, project, worktrees));

        var result = await gateway.SpawnAsync(
            new AgentSpawnRequest(SpawnTarget.NamedByTheAssistant(workspaceId), ProfileLabel: null, ProjectId: project.Id));

        Assert.False(result.Ok);
        Assert.Contains("has no DefaultProfileLabel set", result.Error);
        Assert.Empty(Dispatcher.UIThread.Invoke(() => cockpit.Sessions));
    }

    [Fact]
    public async Task SpawnWithProjectId_WhoseProjectHasNoSourceDirectory_IsNotRefused_AndAppliesBehaviorPromptWithoutAWorkingDirectoryFromTheProject()
    {
        // Raymond's edge case (grooming comment 2026-08-14): a project with no folder of its own is a valid,
        // ordinary input — never a refusal — and never supplies a working directory, which falls back through the
        // same chain it always would (explicit parameter, then the profile's own default).
        var profile = new SessionProfile("work", new ClaudeConfig("/fake/.claude"));
        var project = Project.Create("Admin") with
        {
            SourceDirectories = [],
            DefaultProfileLabel = "work",
            BehaviorPrompt = "Keep the changelog tidy.",
        };
        var worktrees = _WorktreeManagerThatIsolatesCleanly();

        var (gateway, cockpit, _, workspaceId) = Dispatcher.UIThread.Invoke(() => _Gateway(profile, project, worktrees));

        var result = await gateway.SpawnAsync(
            new AgentSpawnRequest(SpawnTarget.NamedByTheAssistant(workspaceId), ProfileLabel: null, ProjectId: project.Id));

        Assert.True(result.Ok, result.Error);
        Assert.Equal("work", result.ResolvedProfileLabel);
        var launch = Dispatcher.UIThread.Invoke(() => cockpit.Sessions.Single().LaunchResult)!;
        Assert.Contains("Keep the changelog tidy.", launch.SystemPrompt);
        Assert.Equal(project.Id, launch.ProjectId);
        Assert.NotEqual(Repository, launch.WorkingDirectory);
        await worktrees.DidNotReceiveWithAnyArgs().CreateForSessionAsync(default!, default, default!);
    }

    private static IWorktreeManager _WorktreeManagerThatIsolatesCleanly()
    {
        var worktrees = Substitute.For<IWorktreeManager>();
        worktrees.ListAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorktreeRecord>());
        worktrees.DetectRepositoryAsync(Repository, Arg.Any<CancellationToken>())
            .Returns(new GitRepositoryInfo(Repository, "abc123", "main"));
        worktrees.CreateForSessionAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<WorktreeSourceHandling>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new WorktreeRecord("owner", Repository, "/repo-wt", "ac-719-branch", "abc123", DateTimeOffset.UnixEpoch)));
        return worktrees;
    }

    private static AgentSpawnRequest _Request(string workspaceId) =>
        new(SpawnTarget.NamedByTheAssistant(workspaceId), "work");

    private static (AssistantAgentGateway Gateway, CockpitViewModel Cockpit, RecordingSpawnTrail Trail, string WorkspaceId) _Gateway(
        SessionProfile profile, Project project, IWorktreeManager worktreeManager, ISessionDialogService? dialogService = null,
        LiveSessionRegistry? liveSessions = null)
    {
        var desk = Workspace.Create("Release", WorkspaceType.Sessions);
        var settings = new WorkspaceSettings { Workspaces = [desk], ActiveWorkspaceId = desk.Id };

        var projectStore = Substitute.For<IProjectStore>();
        projectStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new ProjectSettings { Projects = [project] });
        var dialogs = dialogService ?? Substitute.For<ISessionDialogService>();
        var projects = new ProjectsViewModel(projectStore, dialogs);
        projects.LoadAsync().GetAwaiter().GetResult();

        var profileStore = Substitute.For<ISessionProfileStore>();
        profileStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SessionProfile>>([profile]));
        var mcpCatalog = Substitute.For<IMcpServerCatalog>();
        mcpCatalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns([]);
        var quickStart = new ProjectQuickStart(
            profileStore, mcpCatalog, Substitute.For<ITtySessionProviderResolver>(), new ProjectMemorySourceRegistry());

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
            liveSessions: liveSessions);
        cockpit.Workspaces.Settings = settings;

        var trail = new RecordingSpawnTrail();
        return (
            new AssistantAgentGateway(
                cockpit,
                profileStore,
                trail,
                Substitute.For<IWorkspaceAgentGateway>(),
                Substitute.For<IAgentMessageInbox>(),
                Substitute.For<IAgentNotifyAuditLog>(),
                Substitute.For<IPluginProviderRegistry>(),
                new SessionWatcher(Substitute.For<IAgentMessageInbox>()),
                Substitute.For<IAssistantSessionHost>()),
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
