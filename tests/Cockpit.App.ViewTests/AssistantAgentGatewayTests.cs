using System.Reflection;
using Avalonia.Threading;
using Cockpit.Core.Abstractions;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Assistant;
using Cockpit.Core.Layout;
using Cockpit.Core.Worktrees;
using Cockpit.Core.Notifications;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Projects;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.App.ViewTests;

// AC-545: lives in the view tests for the real dispatcher — without one the marshalling is skipped inline or hangs the host.
[Collection("avalonia")]
public class AssistantAgentGatewayTests
{
    private const string ProfileLabel = "work";

    public sealed class RoleChoices : TheoryData<string, string?, bool, bool, string?, string?, string>
    {
        public RoleChoices()
        {
            Add("absent", null, true, true, null, null, "");
            Add("empty", null, true, true, null, null, "");
            Add("empty", "Programmer", true, false, null, null, "no roles");
            Add("available", null, true, false, null, null, "Programmer: Writes code");
            Add("available", null, false, true, null, null, "");
            Add("available", "none", true, true, "none", null, "");
            Add("available", "Programmer", true, true, "Programmer", null, "");
            Add("available", "missing", true, false, null, null, "Programmer: Writes code");
            Add("unavailable", null, true, true, null, "unavailable", "");
            Add("unavailable", "none", true, true, "none", "unavailable", "");
            Add("unavailable", "Programmer", true, false, null, null, "unavailable");
            Add("throws", null, true, true, null, "unavailable", "");
        }
    }

    [Theory]
    [ClassData(typeof(RoleChoices))]
    public async Task Spawn_RequiresAConsciousRoleChoiceOnlyAtTheRequestingDoor(
        string source, string? role, bool required, bool expectedOk, string? expectedRole, string? expectedList, string errorHint)
    {
        var desk = _Desk("Sessions", WorkspaceType.Sessions);
        var launcher = Substitute.For<ISessionLauncher>();
        launcher.Workspaces.Returns(_Settings(desk));
        launcher.RunExclusiveAsync(Arg.Any<Func<Workspace?>>()).Returns(call => call.Arg<Func<Workspace?>>()());
        var project = new Project("target", "Target") { Assistant = "roles:org/target@server" };
        launcher.FindProjectByIdAsync(project.Id).Returns(project);
        launcher.StartSessionAsync(Arg.Any<SessionLaunchRequest>()).Returns(new LaunchedSession("pane", "Started", true));
        var profiles = Substitute.For<ISessionProfileStore>();
        profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new SessionProfile(ProfileLabel, new ClaudeConfig("unused")) { Assistant = "roles:org/assistant@server" },
        });
        var registry = new ProjectMemorySourceRegistry();
        registry.Register(new Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceRegistration("roles", "Roles", "Read memory")
        {
            BrainInstruction = _ => "Load the chosen brain",
            ListRolesAsync = _RoleSource(source),
        });
        var trail = new RecordingSpawnTrail();
        var gateway = new AssistantAgentGateway(
            new SessionRegistry(), launcher, TestProjects.Catalog(), Substitute.For<IProjectComposer>(),
            Substitute.For<ISessionWatcher>(), Substitute.For<IAssistantConversation>(), Substitute.For<IExternalLinkOpener>(),
            profiles, trail, Substitute.For<IWorkspaceAgentGateway>(), Substitute.For<IAgentMessageInbox>(),
            Substitute.For<IAgentNotifyAuditLog>(), Substitute.For<IPluginProviderRegistry>(), Substitute.For<IProviderModelIntelligence>(),
            roles: new SessionRoleResolver(registry));

        var result = await gateway.SpawnAsync(new AgentSpawnRequest(
            SpawnTarget.RequestedByThePairedController(desk.Id), ProfileLabel, project.Id, Role: role, RoleChoiceRequired: required));

        Assert.Equal(expectedOk, result.Ok);
        Assert.Equal(expectedRole, result.Role);
        Assert.Equal(expectedList, result.RoleList);
        Assert.Contains(errorHint, result.Error ?? string.Empty);
        Assert.Equal(role, Assert.Single(trail.Entries).Role);
        await launcher.Received(expectedOk ? 1 : 0).StartSessionAsync(Arg.Is<SessionLaunchRequest>(request =>
            request.ProjectId == project.Id && request.Role == (expectedRole == "none" ? null : expectedRole)));
    }

    private static Func<string, CancellationToken, Task<Cockpit.Plugins.Abstractions.Projects.RoleListResult>>? _RoleSource(string source) => source switch
    {
        "absent" => null,
        "empty" => (_, _) => Task.FromResult(new Cockpit.Plugins.Abstractions.Projects.RoleListResult(Cockpit.Plugins.Abstractions.Projects.RoleSourceOutcome.NoRoles, [])),
        "unavailable" => (_, _) => Task.FromResult(new Cockpit.Plugins.Abstractions.Projects.RoleListResult(Cockpit.Plugins.Abstractions.Projects.RoleSourceOutcome.Unavailable, [])),
        "throws" => (_, _) => throw new InvalidOperationException("source offline"),
        _ => (brain, _) =>
        {
            Assert.Equal("org/target@server", brain);
            return Task.FromResult(new Cockpit.Plugins.Abstractions.Projects.RoleListResult(Cockpit.Plugins.Abstractions.Projects.RoleSourceOutcome.Success,
                [new("Programmer", "Writes code"), new("none", "Reserved")]));
        },
    };

    [Fact]
    public async Task SpawnOntoAWorkspaceIdThatNamesNothing_IsRefusedWithAReason_AndTheRefusalReachesTheTrail()
    {
        var (gateway, _, trail) = Dispatcher.UIThread.Invoke(
            () => _Gateway(_Settings(_Desk("Sessions", WorkspaceType.Sessions))));

        var result = await gateway.SpawnAsync(_Request("no-such-desk"));

        Assert.False(result.Ok);
        Assert.Null(result.PaneId);
        Assert.Contains("no workspace with id 'no-such-desk'", result.Error);

        var entry = Assert.Single(trail.Entries);
        Assert.Equal(AssistantSpawnAction.Start, entry.Action);
        Assert.Equal(SpawnCaller.Assistant, entry.Caller);
        Assert.Equal("no-such-desk", entry.WorkspaceId);
        Assert.Null(entry.PaneId);
        Assert.Equal(result.Error, entry.Refusal);
    }

    public static TheoryData<string> EveryWorkspaceTypeThatCannotHostASession()
    {
        var data = new TheoryData<string>();
        foreach (var type in _AllKnownWorkspaceTypes().Where(type => type != WorkspaceType.Sessions))
        {
            data.Add(type.Id);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryWorkspaceTypeThatCannotHostASession))]
    public async Task SpawnOntoADeskThatIsNotASessionsDesk_IsRefused_AndTheRefusalReachesTheTrail(string workspaceTypeId)
    {
        // A dashboard (or a projects overview, or a plugin's own desk) would take the session and never draw it:
        // the pane would run, cost money and be unreachable. Refusing is the kinder half of that pair.
        var desk = _Desk("Monitoring", WorkspaceType.FromId(workspaceTypeId));
        var (gateway, _, trail) = Dispatcher.UIThread.Invoke(() => _Gateway(_Settings(desk)));

        var result = await gateway.SpawnAsync(_Request(desk.Id));

        Assert.False(result.Ok);
        Assert.Null(result.PaneId);
        Assert.Contains("cannot show a session", result.Error);

        var entry = Assert.Single(trail.Entries);
        Assert.Equal(desk.Id, entry.WorkspaceId);
        Assert.Equal("Monitoring", entry.WorkspaceName);
        Assert.Equal(result.Error, entry.Refusal);
    }

    [Fact]
    public async Task SpawnThatTriesToSetThePermissionMode_IsRefused_AndNothingIsStarted()
    {
        // AC-648 criterion 3 (Raymond, 2026-08-08), asserted on the whole spawn path rather than on the merge alone:
        // the claim is that the request is turned away, not that the mode happened to survive it. A spawn that
        // stripped the key and started anyway would leave the assistant reporting a session it never got.
        var desk = _Desk("Release", WorkspaceType.Sessions);
        var (gateway, cockpit, trail) = Dispatcher.UIThread.Invoke(
            () => _Gateway(_Settings(desk), host: null, _PluginProfile(("permission-mode", "default")), _ClaudeRegistry()));
        var before = Dispatcher.UIThread.Invoke(() => cockpit.Sessions.Count);

        var result = await gateway.SpawnAsync(_Request(desk.Id) with
        {
            OptionOverrides = new Dictionary<string, string> { ["permission-mode"] = "bypassPermissions" },
        });

        Assert.False(result.Ok);
        Assert.Null(result.PaneId);
        Assert.Contains("not something a spawn may set", result.Error);
        Assert.Equal(before, Dispatcher.UIThread.Invoke(() => cockpit.Sessions.Count));
        Assert.Equal(result.Error, Assert.Single(trail.Entries).Refusal);
    }

    [Fact]
    public async Task SpawnWithAnOptionKeyTheProviderNeverDeclared_IsRefusedWithWhatItDoesTake()
    {
        // Criterion 2. The refusal is the whole point of AC-649 landing first: without a declared schema this would
        // be accepted and handed to a CLI that has no such flag, and the session would come up wrong.
        var desk = _Desk("Release", WorkspaceType.Sessions);
        var (gateway, _, _) = Dispatcher.UIThread.Invoke(
            () => _Gateway(_Settings(desk), host: null, _PluginProfile(), _ClaudeRegistry()));

        var result = await gateway.SpawnAsync(_Request(desk.Id) with
        {
            OptionOverrides = new Dictionary<string, string> { ["sandbox-policy"] = "read-only" },
        });

        Assert.False(result.Ok);
        Assert.Contains("no option called 'sandbox-policy'", result.Error);
        Assert.Contains("'effort'", result.Error);
    }

    [Fact]
    public async Task SpawnWithAProfileLabelTheCockpitDoesNotHave_IsRefused_NamesTheOnesItDoes_AndReachesTheTrail()
    {
        // By label and never by "the first one that looks close": the profile decides provider and model, so a
        // near-miss is a bill the operator did not agree to.
        var desk = _Desk("Release", WorkspaceType.Sessions);
        var (gateway, _, trail) = Dispatcher.UIThread.Invoke(() => _Gateway(_Settings(desk)));

        var result = await gateway.SpawnAsync(_Request(desk.Id) with { ProfileLabel = "opus-everything" });

        Assert.False(result.Ok);
        Assert.Contains("no profile called 'opus-everything'", result.Error);
        Assert.Contains($"'{ProfileLabel}'", result.Error);

        var entry = Assert.Single(trail.Entries);
        Assert.Equal("opus-everything", entry.Profile);
        Assert.Equal(desk.Id, entry.WorkspaceId);
        Assert.Equal(result.Error, entry.Refusal);
    }

    [Fact]
    public async Task StoppingTheAssistantsOwnPane_IsRefusedByIdentity_EvenWhenThatPaneIsPerfectlyFindable()
    {
        // Asserted by identity rather than by "it happens not to be in Sessions": the assistant sits outside both
        // session collections today, so a check that leaned on that would pass for a reason that is a placement
        // and not a rule. Here the pane is deliberately findable and an ordinary agent session in every other
        // respect — the only thing wrong with it is who it is.
        var desk = _Desk("Release", WorkspaceType.Sessions);
        var (gateway, cockpit, trail) = Dispatcher.UIThread.Invoke(() => _Gateway(_Settings(desk)));
        Dispatcher.UIThread.Invoke(() =>
        {
            var impostor = new SessionViewModel { WorkspaceId = desk.Id, Title = "The assistant" };
            impostor.AdoptPaneId(AssistantIdentity.PaneId);
            cockpit.Sessions.Add(impostor);
            Assert.NotNull(cockpit.FindSession(AssistantIdentity.PaneId));
        });

        var result = await gateway.StopAsync(AssistantIdentity.PaneId);

        Assert.False(result.Ok);
        Assert.Contains("my own session", result.Error);
        Dispatcher.UIThread.Invoke(
            () => Assert.Contains(cockpit.Sessions, session => session.PaneId == AssistantIdentity.PaneId));

        var entry = Assert.Single(trail.Entries);
        Assert.Equal(AssistantSpawnAction.Stop, entry.Action);
        Assert.Equal(AssistantIdentity.PaneId, entry.PaneId);
        Assert.Equal(result.Error, entry.Refusal);
    }

    [Fact]
    public async Task Handover_WorktreeOwnedByADifferentSession_IsRefused()
    {
        // Never a confused deputy: only the assistant's own worktrees may be given away, whoever asks.
        var worktrees = Substitute.For<IWorktreeManager>();
        var record = new WorktreeRecord("someone-elses-pane", "/repo", "/wt/theirs", "cockpit/x", "abc", DateTimeOffset.UtcNow);
        worktrees.ListAsync(Arg.Any<CancellationToken>()).Returns(new List<WorktreeRecord> { record });
        var (gateway, _, trail) = Dispatcher.UIThread.Invoke(
            () => _Gateway(_Settings(_Desk("Release", WorkspaceType.Sessions)), worktreeManager: worktrees));

        var result = await gateway.HandoverWorktreeAsync("/wt/theirs", "pane-target");

        Assert.False(result.Ok);
        Assert.Contains("not mine to hand over", result.Error);
        Assert.Single(trail.Entries);
        await worktrees.DidNotReceiveWithAnyArgs().ReattachAsync(default!, default!);
    }

    // ── AC-587: open_url's own door onto ExternalLink ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url")]
    public async Task OpenUrl_ANonWebScheme_IsRefused_AndStartsNothing(string url)
    {
        var (gateway, _, _) = Dispatcher.UIThread.Invoke(
            () => _Gateway(_Settings(_Desk("Release", WorkspaceType.Sessions))));

        var result = await gateway.OpenUrlAsync(url);

        Assert.False(result.Ok);
        Assert.Null(result.Url);
        Assert.Contains("not an absolute http(s) address", result.Error);
    }

    [Fact]
    public async Task ASpawnOntoANamedDesk_LandsThere_AndLeavesTheOperatorOnTheDeskTheyWereLookingAt()
    {
        // The whole reason the assistant is asked to set work up somewhere else: it must not drag the operator
        // off what is on screen. Three separate ways that could happen, all pinned — the active desk, the
        // selected session, and where the pane actually ends up.
        var here = _Desk("Here", WorkspaceType.Sessions);
        var elsewhere = _Desk("Release", WorkspaceType.Sessions);
        var (gateway, cockpit, _) = Dispatcher.UIThread.Invoke(() => _Gateway(_Settings(here, elsewhere)));
        var watching = Dispatcher.UIThread.Invoke(() =>
        {
            var session = new SessionViewModel { WorkspaceId = here.Id, Title = "What the operator is reading" };
            cockpit.Sessions.Add(session);
            cockpit.SelectedSession = session;
            Assert.Equal(here.Id, cockpit.Workspaces.Settings.Active?.Id);
            return session;
        });

        var result = await gateway.SpawnAsync(_Request(elsewhere.Id));

        Assert.True(result.Ok, result.Error);
        Assert.NotNull(result.PaneId);
        var spawned = Dispatcher.UIThread.Invoke(() => cockpit.FindSession(result.PaneId!));
        Assert.NotNull(spawned);
        Assert.Equal(elsewhere.Id, spawned!.WorkspaceId);

        Dispatcher.UIThread.Invoke(() =>
        {
            Assert.Equal(here.Id, cockpit.Workspaces.Settings.Active?.Id);
            Assert.Same(watching, cockpit.SelectedSession);
        });
    }

    // --- Removing a desk: the counterpart of create_workspace, and the tab's own ✕ ----------------------------

    [Fact]
    public async Task RemovingADeskWithSessionsOnIt_IsRefused_AndNeitherTheDeskNorItsSessionsAreTouched()
    {
        // The guarantee the tool's description makes, attacked directly. Closing a desk stops everything on it in
        // one go, and what the operator approved was a row naming a desk — so the sessions go first, through
        // stop_agent and an approval each. Both halves are asserted: the reason, and that nothing was already
        // half-done by the time it came back.
        var here = _Desk("Here", WorkspaceType.Sessions);
        var busy = _Desk("Henk", WorkspaceType.Sessions);
        var (gateway, cockpit, _) = Dispatcher.UIThread.Invoke(() =>
        {
            var built = _Gateway(_Settings(here, busy));
            built.Cockpit.Sessions.Add(new SessionViewModel { WorkspaceId = busy.Id, Title = "AC-545" });
            built.Cockpit.Sessions.Add(new SessionViewModel { WorkspaceId = busy.Id, Title = "AC-546" });
            return built;
        });

        var result = await gateway.RemoveWorkspaceAsync(busy.Id);

        Assert.False(result.Ok);
        Assert.Equal("There are still 2 sessions on 'Henk'. Stop them first — I do not close a desk with work still on it.", result.Error);

        Dispatcher.UIThread.Invoke(() =>
        {
            Assert.Contains(cockpit.Workspaces.Settings.Workspaces, workspace => workspace.Id == busy.Id);
            Assert.Equal(2, cockpit.Sessions.Count(session => session.WorkspaceId == busy.Id));
        });
    }

    [Fact]
    public async Task RemovingADeskThatHoldsOnlyAPlainTerminal_IsStillRefused_ThoughTheRosterCountsItAsEmpty()
    {
        // The one place this check may not simply reuse list_workspaces' number. A terminal is not an agent
        // session, so the roster reports the desk as empty — but closing the desk kills its pty just the same, and
        // that is precisely the work nobody asked to lose. Both facts are pinned in one test, because the second
        // only means something next to the first.
        var here = _Desk("Here", WorkspaceType.Sessions);
        var withATerminal = _Desk("Henk", WorkspaceType.Sessions);
        var (gateway, cockpit, _) = Dispatcher.UIThread.Invoke(() =>
        {
            var built = _Gateway(_Settings(here, withATerminal));
            built.Cockpit.Sessions.Add(new TtyViewModel { WorkspaceId = withATerminal.Id, Title = "pwsh", ShowPluginHeaderItems = false });
            return built;
        });

        Assert.Equal(0, (await gateway.ListWorkspacesAsync()).Single(row => row.Id == withATerminal.Id).SessionCount);

        var result = await gateway.RemoveWorkspaceAsync(withATerminal.Id);

        Assert.False(result.Ok);
        Assert.Contains("still 1 session on 'Henk'", result.Error);
        Dispatcher.UIThread.Invoke(
            () => Assert.Contains(cockpit.Workspaces.Settings.Workspaces, workspace => workspace.Id == withATerminal.Id));
    }

    [Fact]
    public async Task RenamingTheAssistantsOwnSession_IsRefused_EvenWhenItSitsInTheRoster()
    {
        // Stamped into Sessions on purpose: the guard has to be the identity check, not the accident that the
        // assistant is in no collection today. Same shape as the stop-side test above, and for the same reason.
        var desk = _Desk("Release", WorkspaceType.Sessions);
        var (gateway, cockpit, _) = Dispatcher.UIThread.Invoke(() =>
        {
            var built = _Gateway(_Settings(desk));
            var assistant = new SessionViewModel { WorkspaceId = desk.Id, Title = "The assistant" };
            assistant.AdoptPaneId(AssistantIdentity.PaneId);
            built.Cockpit.Sessions.Add(assistant);
            return built;
        });

        var result = await gateway.RenameSessionAsync(AssistantIdentity.PaneId, "Luna");

        Assert.False(result.Ok);
        Assert.Contains("my own session", result.Error);
        Dispatcher.UIThread.Invoke(() => Assert.Equal(
            "The assistant",
            cockpit.Sessions.Single(session => session.PaneId == AssistantIdentity.PaneId).Title));
    }

    // --- The graph under test -------------------------------------------------------------------------------

    // WorkspaceType is open on purpose (a record struct over a string), so the rule must hold for types no test author has seen.
    private static IReadOnlyList<WorkspaceType> _AllKnownWorkspaceTypes() =>
    [
        .. typeof(WorkspaceType)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(WorkspaceType))
            .Select(property => (WorkspaceType)property.GetValue(null)!),
        WorkspaceType.FromId("acme.autopilot"),
    ];

    private static AgentSpawnRequest _Request(string workspaceId) =>
        new(SpawnTarget.NamedByTheAssistant(workspaceId), ProfileLabel);

    private static Workspace _Desk(string name, WorkspaceType type) => Workspace.Create(name, type);

    private static WorkspaceSettings _Settings(params Workspace[] workspaces) =>
        new() { Workspaces = workspaces, ActiveWorkspaceId = workspaces[0].Id };

    private static (AssistantAgentGateway Gateway, CockpitViewModel Cockpit, RecordingSpawnTrail Trail) _Gateway(
        WorkspaceSettings settings,
        CockpitViewModel? host = null,
        // The profile a spawn runs under and the registry its provider is resolved through — defaulted to a plain
        // Claude-config profile, which is what every test that is not about provider options wants.
        SessionProfile? profile = null,
        IPluginProviderRegistry? pluginProviders = null,
        IWorktreeManager? worktreeManager = null)
    {
        var sessions = new SessionRegistry();
        var cockpit = host ?? _Cockpit(sessions);
        cockpit.Workspaces.Settings = settings;

        var profiles = Substitute.For<ISessionProfileStore>();
        profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SessionProfile>>(
            [profile ?? new SessionProfile(ProfileLabel, new ClaudeConfig(@"C:\fake\.claude"))]));

        var trail = new RecordingSpawnTrail();
        return (
            AssistantAgentGatewayGraph.Over(
                cockpit,
                sessions,
                profiles,
                trail,
                Substitute.For<IWorkspaceAgentGateway>(),
                Substitute.For<IAgentMessageInbox>(),
                Substitute.For<IAgentNotifyAuditLog>(),
                pluginProviders ?? Substitute.For<IPluginProviderRegistry>(),
                new SessionWatcher(Substitute.For<IAgentMessageInbox>()),
                Substitute.For<IAssistantSessionHost>(),
                worktreeManager),
            cockpit,
            trail);
    }

    // A plugin-backed profile under the test's own label, with whatever start defaults the caller wants on it —
    // the only shape that resolves to a provider registration, and so the only one an override can be checked against.
    private static SessionProfile _PluginProfile(params (string Key, string Value)[] optionDefaults) =>
        new(ProfileLabel, new PluginProviderConfig("claude", "{}"))
        {
            Defaults = new ProfileDefaults(string.Empty, string.Empty, string.Empty, AutoApproveTools: false)
            {
                OptionDefaults = optionDefaults.ToDictionary(option => option.Key, option => option.Value),
            },
        };

    // Claude's three declared options (AC-649), as the real plugin registers them.
    private static IPluginProviderRegistry _ClaudeRegistry()
    {
        var registry = Substitute.For<IPluginProviderRegistry>();
        registry.Resolve("claude").Returns(_ProviderRegistration(
            "claude",
            "Claude",
            new PluginSessionOptionDescriptor("permission-mode", "Permission mode",
                [new PluginSessionOptionValue("default", "Ask permissions"), new PluginSessionOptionValue("bypassPermissions", "Bypass permissions")], "default"),
            new PluginSessionOptionDescriptor("model", "Model"),
            new PluginSessionOptionDescriptor("effort", "Effort",
                [new PluginSessionOptionValue("low", "Low"), new PluginSessionOptionValue("high", "High")], "medium")));
        return registry;
    }

    private static SessionProviderRegistration _ProviderRegistration(
        string providerId, string displayName, params PluginSessionOptionDescriptor[] declaredOptions) =>
        new(
            providerId,
            displayName,
            _ => throw new NotSupportedException("Nothing here starts a session."),
            new PluginSessionCapabilities(SupportsTools: true, SupportsPermissions: true) { DeclaredOptions = declaredOptions });

    private static CockpitViewModel _Cockpit(SessionRegistry? sessions = null)
    {
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

        return new CockpitViewModel(
            () => new SessionViewModel(),
            () => new TtyViewModel(),
            Substitute.For<ISessionDialogService>(),
            Substitute.For<IAudioCaptureService>(),
            Substitute.For<IAudioPlaybackService>(),
            Substitute.For<IAttentionNotifier>(),
            notificationSettingsStore,
            transcriptDisplaySettingsStore,
            sessionBehaviorSettingsStore,
            layoutSettingsStore,
            voiceSettingsStore,
            terminalSettingsStore,
            sessionRegistry: sessions);
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
