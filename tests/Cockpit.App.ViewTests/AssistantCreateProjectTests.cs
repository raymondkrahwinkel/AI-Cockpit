using Avalonia.Threading;
using Cockpit.App.Plugins;
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
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-799: <c>AssistantAgentGateway.CreateProjectAsync</c> — "New project" without the window. What it produces
/// has to pass the dialog's own <c>CanSave</c>/<c>ToProject</c>, never a rewritten copy of that rule, and a name
/// already shared elsewhere has to be reported rather than quietly duplicated next to it.
/// </summary>
[Collection("avalonia")]
public class AssistantCreateProjectTests : IDisposable
{
    private const string ProfileLabel = "Zyra";

    private readonly string _folder = Directory.CreateTempSubdirectory("ac799-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    // ── Criterion 1: the full field surface, and the id comes back ─────────────────────────────────────────────

    [Fact]
    public async Task Creates_WithTheFullFieldSurface_AndReturnsTheId()
    {
        var (gateway, projects, store) = _Build(
            pluginFields: [new ProjectFieldRegistration("youtrack.project", "YouTrack project", _ => Task.FromResult<IReadOnlyList<ProjectFieldOption>>([]))]);

        var result = await gateway.CreateProjectAsync(
            "Invoices",
            description: "Client billing",
            sourceDirectory: _folder,
            defaultProfileLabel: ProfileLabel,
            behaviorPrompt: "Write in Dutch.",
            isolateInWorktreeByDefault: true,
            enabledMcpServerNames: ["depot"],
            category: "Werk",
            pluginFields: new Dictionary<string, string> { ["youtrack.project"] = "AC" });

        Assert.True(result.Ok, result.Error);
        Assert.Equal("Invoices", result.Name);
        Assert.NotNull(result.ProjectId);

        var stored = Assert.Single(projects.Projects);
        Assert.Equal(result.ProjectId, stored.Id);
        Assert.Equal("Invoices", stored.Name);
        Assert.Equal("Client billing", stored.Description);
        Assert.Equal(_folder, stored.SourceDirectory);
        Assert.Equal(ProfileLabel, stored.DefaultProfileLabel);
        Assert.Equal("Write in Dutch.", stored.BehaviorPrompt);
        Assert.True(stored.IsolateInWorktreeByDefault);
        Assert.Equal(["depot"], stored.McpOverlay.EnabledServerNames);
        Assert.Equal("Werk", stored.Category);
        Assert.Equal("AC", stored.PluginFields["youtrack.project"]);

        // Criterion 3's other half, demonstrable from out here too: what reached the store is the projects view
        // model's own persisting — one normalised save of the whole settings — not a write this gateway composed.
        await store.Received(1).SaveAsync(Arg.Is<ProjectSettings>(settings =>
            settings.Projects.Count == 1 && settings.Projects[0].Name == "Invoices"));
    }

    [Fact]
    public async Task SourceDirectoryLeftOut_CreatesAnAdministrativeProject()
    {
        var (gateway, projects, _) = _Build();

        var result = await gateway.CreateProjectAsync("Standup notes");

        Assert.True(result.Ok, result.Error);
        var stored = Assert.Single(projects.Projects);
        Assert.Null(stored.SourceDirectory);
    }

    // ── Criterion 2: the same validation as the dialog, demonstrated against the dialog's own view model ────────

    [Fact]
    public async Task BlankName_IsRefused_ForTheSameReasonTheDialogsSaveButtonWouldStayDisabled()
    {
        var (gateway, projects, _) = _Build();

        var result = await gateway.CreateProjectAsync("   ");

        Assert.False(result.Ok);
        Assert.Equal("A project needs a name.", result.Error);
        Assert.Empty(projects.Projects);

        // Not assumed: the dialog's own view model, given the same blank name, has CanSave false for the identical
        // reason — this is `ProjectDialogViewModel.CanSave` itself, not a rewritten copy of its rule.
        var dialogViewModel = await Dispatcher.UIThread.InvokeAsync(
            () => ProjectDialogViewModel.CreateAsync(null, _Profiles(), _McpCatalog()));
        dialogViewModel.Name = "   ";
        Assert.False(dialogViewModel.CanSave);
    }

    [Fact]
    public async Task ANameTheDialogWouldAccept_TheGatewayAcceptsToo()
    {
        // The other half of criterion 2: proving the gateway does not refuse something the dialog's own CanSave
        // would let through — a mismatched-but-stricter gate is as much a second source of truth as a looser one.
        var dialogViewModel = await Dispatcher.UIThread.InvokeAsync(
            () => ProjectDialogViewModel.CreateAsync(null, _Profiles(), _McpCatalog()));
        dialogViewModel.Name = "  Invoices  ";
        Assert.True(dialogViewModel.CanSave);

        var (gateway, _, _) = _Build();
        var result = await gateway.CreateProjectAsync("  Invoices  ");

        Assert.True(result.Ok, result.Error);
        Assert.Equal("Invoices", result.Name);
    }

    // ── Criterion 3: no read-modify-write race with an operator's own dialog save, demonstrated ────────────────

    [Fact]
    public async Task NoRaceWithAConcurrentDialogSave_BothProjectsSurvive()
    {
        // AC-799 review finding 2: a store whose `SaveAsync` is already a completed `Task` (a bare
        // `Substitute.For<IProjectStore>()`, as this test used to build) never actually interleaves the two
        // dispatcher jobs below — an already-completed `Task` does not yield control on `await`, so the first call
        // would run start to finish, `_logos` being null making `_WithStoredLogoAsync` synchronous too, before the
        // second job ever gets a turn. That "proved" the assertion by never exercising the race at all. Here
        // `SaveAsync` genuinely suspends the caller until this test releases it, so the dispatcher is forced to run
        // the second job while the first is still awaiting inside `_PersistAsync` — the actual interleaving a lost
        // update would need.
        var firstSaveReached = new TaskCompletionSource();
        var secondSaveReached = new TaskCompletionSource();
        var releaseSaves = new TaskCompletionSource();
        var saveCalls = 0;

        var store = Substitute.For<IProjectStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(ProjectSettings.Empty);
        store.SaveAsync(Arg.Any<ProjectSettings>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            (Interlocked.Increment(ref saveCalls) == 1 ? firstSaveReached : secondSaveReached).SetResult();
            return releaseSaves.Task;
        });

        var projects = new ProjectsViewModel(store, dialogs: null);
        await Dispatcher.UIThread.InvokeAsync(() => projects.LoadAsync());

        var assistantProject = Project.Create("Assistant project") with { SourceDirectories = [new(_folder)] };
        var dialogProject = Project.Create("Dialog project") with { SourceDirectories = [new(_folder)] };

        var assistantTask = Dispatcher.UIThread.InvokeAsync(() => projects.AddNewProjectAsync(assistantProject));
        var dialogTask = Dispatcher.UIThread.InvokeAsync(() => projects.AddNewProjectAsync(dialogProject));

        // Both jobs have now reached `_PersistAsync`'s `await _store.SaveAsync(settings)` and are suspended there —
        // which proves the second call's `_settings.WithProject(stored)` read ran only after the first call's
        // `_settings = settings;` write, exactly the ordering the production comment on `AddNewProjectAsync`
        // depends on (see AC-799 review finding 7).
        await Task.WhenAll(firstSaveReached.Task, secondSaveReached.Task);

        releaseSaves.SetResult();
        await Task.WhenAll(assistantTask, dialogTask);

        Assert.Equal(2, projects.Projects.Count);
        Assert.Contains(projects.Projects, project => project.Name == "Assistant project");
        Assert.Contains(projects.Projects, project => project.Name == "Dialog project");
    }

    // ── Criterion 6: a name already shared elsewhere is reported, not duplicated ────────────────────────────────

    [Fact]
    public async Task ANameAlreadySharedElsewhere_IsRefused_RatherThanDuplicated()
    {
        var (gateway, projects, _) = _Build(sharedProjects: [new SharedProject("depot:handbook", "Handbook")]);

        var result = await gateway.CreateProjectAsync("Handbook");

        Assert.False(result.Ok);
        Assert.Contains("Handbook", result.Error);
        Assert.Contains("Depot — Work", result.Error);
        Assert.Contains("bind_shared_project", result.Error);
        Assert.Empty(projects.Projects);
    }

    [Fact]
    public async Task ANameSharedElsewhereButAlreadyBoundHere_IsNotTreatedAsACollision()
    {
        // Already bound is a project on this machine already, not a name this call is about to duplicate — the
        // same visibility filter `list_shared_projects` itself applies (AC-797's `SharedProjectVisibilityFilterIds`).
        var bound = new Project("local-1", "Handbook")
        {
            Resources = [new ProjectResource("depot:handbook", ProjectResourceRole.Memory)],
        };
        var (gateway, projects, _) = _Build(
            sharedProjects: [new SharedProject("depot:handbook", "Handbook")],
            settings: ProjectSettings.Empty with { Projects = [bound] });

        var result = await gateway.CreateProjectAsync("Handbook");

        Assert.True(result.Ok, result.Error);
        Assert.Equal(2, projects.Projects.Count);
    }

    [Fact]
    public async Task ASecondLocalProjectWithTheSameNameAsAnExistingLocalOne_IsAllowed()
    {
        // Unchanged behaviour (`Project.Name`'s own doc comment: "free to collide with another project's name") —
        // this tool only guards against duplicating what a connection shares, not against two local projects
        // sharing a name, which was always permitted.
        var existing = Project.Create("Invoices") with { SourceDirectories = [new(_folder)] };
        var (gateway, projects, _) = _Build(settings: ProjectSettings.Empty with { Projects = [existing] });

        var result = await gateway.CreateProjectAsync("Invoices");

        Assert.True(result.Ok, result.Error);
        Assert.Equal(2, projects.Projects.Count(project => project.Name == "Invoices"));
    }

    // ── sourceDirectory: full path, and it has to exist ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ARelativeSourceDirectory_IsRefused()
    {
        var (gateway, projects, _) = _Build();

        var result = await gateway.CreateProjectAsync("Invoices", sourceDirectory: "relative/path");

        Assert.False(result.Ok);
        Assert.Contains("relative path", result.Error);
        Assert.Empty(projects.Projects);
    }

    [Fact]
    public async Task ASourceDirectoryThatDoesNotExist_IsRefused()
    {
        var (gateway, projects, _) = _Build();

        var result = await gateway.CreateProjectAsync("Invoices", sourceDirectory: Path.Combine(_folder, "does-not-exist"));

        Assert.False(result.Ok);
        Assert.Contains("no folder", result.Error);
        Assert.Empty(projects.Projects);
    }

    // ── pluginFields: keys come from the registry, never invented ──────────────────────────────────────────────

    [Fact]
    public async Task AnUnknownPluginFieldKey_IsRefused_AndNamesTheOnesThatAreRegistered()
    {
        var (gateway, projects, _) = _Build(
            pluginFields: [new ProjectFieldRegistration("youtrack.project", "YouTrack project", _ => Task.FromResult<IReadOnlyList<ProjectFieldOption>>([]))]);

        var result = await gateway.CreateProjectAsync(
            "Invoices", pluginFields: new Dictionary<string, string> { ["github.repository"] = "acme/invoices" });

        Assert.False(result.Ok);
        Assert.Contains("github.repository", result.Error);
        Assert.Contains("youtrack.project", result.Error);
        Assert.Empty(projects.Projects);
    }

    // AC-884: a value naming several prefixes is stored and read back verbatim — the tool takes the same
    // Dictionary<string,string> it always did, the comma-separated list is a convention this layer never parses.
    [Fact]
    public async Task APluginFieldValueNamingSeveralPrefixes_IsStoredAndReadableAsEachOfThem()
    {
        var (gateway, projects, _) = _Build(
            pluginFields: [new ProjectFieldRegistration("youtrack.project", "YouTrack project", _ => Task.FromResult<IReadOnlyList<ProjectFieldOption>>([]))]);

        var result = await gateway.CreateProjectAsync(
            "EVE Workbench", pluginFields: new Dictionary<string, string> { ["youtrack.project"] = "EWB, AT, EJ" });

        Assert.True(result.Ok, result.Error);
        var stored = Assert.Single(projects.Projects);
        Assert.Equal(["EWB", "AT", "EJ"], stored.LinkedAsAll("youtrack.project"));
    }

    // ── defaultProfileLabel: validated like every sibling path in this class (AC-799 review finding 3) ────────────

    [Fact]
    public async Task AnUnknownDefaultProfileLabel_IsRefused_AndNamesTheOnesThatAreConfigured()
    {
        var (gateway, projects, _) = _Build();

        var result = await gateway.CreateProjectAsync("Invoices", defaultProfileLabel: "not-a-real-profile");

        Assert.False(result.Ok);
        Assert.Contains("not-a-real-profile", result.Error);
        Assert.Contains(ProfileLabel, result.Error);
        Assert.Empty(projects.Projects);
    }

    [Fact]
    public async Task AKnownDefaultProfileLabel_IsAccepted()
    {
        var (gateway, projects, _) = _Build();

        var result = await gateway.CreateProjectAsync("Invoices", defaultProfileLabel: ProfileLabel);

        Assert.True(result.Ok, result.Error);
        var stored = Assert.Single(projects.Projects);
        Assert.Equal(ProfileLabel, stored.DefaultProfileLabel);
    }

    // ── mcpServerCatalog: a refusal, not a silent no-op catalog (AC-799 review finding 8) ──────────────────────────

    [Fact]
    public async Task WithNoMcpServerCatalog_ItRefuses_RatherThanFallingBackToANoOpOne()
    {
        var (gateway, projects, _) = _Build(includeMcpCatalog: false);

        var result = await gateway.CreateProjectAsync("Invoices");

        Assert.False(result.Ok);
        Assert.Empty(projects.Projects);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────────────────────────────────────

    private (AssistantAgentGateway Gateway, ProjectsViewModel Projects, IProjectStore Store) _Build(
        IReadOnlyList<SharedProject>? sharedProjects = null,
        IReadOnlyList<ProjectFieldRegistration>? pluginFields = null,
        ProjectSettings? settings = null,
        bool includeMcpCatalog = true)
    {
        var store = Substitute.For<IProjectStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(settings ?? ProjectSettings.Empty);

        var registry = sharedProjects is null
            ? new _FakeRegistry([])
            : new _FakeRegistry([_Source(sharedProjects)]);

        var fieldRegistry = new ProjectFieldRegistry();
        foreach (var field in pluginFields ?? [])
        {
            fieldRegistry.Register(field);
        }

        var projects = new ProjectsViewModel(store, dialogs: null, sharedSources: registry);
        Dispatcher.UIThread.Invoke(() => projects.LoadAsync()).GetAwaiter().GetResult();

        var gateway = Dispatcher.UIThread.Invoke(() => new AssistantAgentGateway(
            _NewCockpit(projects),
            _Profiles(),
            Substitute.For<IAssistantSpawnAuditLog>(),
            Substitute.For<IWorkspaceAgentGateway>(),
            Substitute.For<IAgentMessageInbox>(),
            Substitute.For<IAgentNotifyAuditLog>(),
            Substitute.For<IPluginProviderRegistry>(),
            new SessionWatcher(Substitute.For<IAgentMessageInbox>()),
            worktreeManager: null,
            sharedProjectSources: registry,
            mcpServerCatalog: includeMcpCatalog ? _McpCatalog() : null,
            projectFields: fieldRegistry));

        return (gateway, projects, store);
    }

    private static IMcpServerCatalog _McpCatalog()
    {
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Cockpit.Core.Mcp.McpServerConfig>>([]));
        return catalog;
    }

    private static ISessionProfileStore _Profiles()
    {
        var profiles = Substitute.For<ISessionProfileStore>();
        profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SessionProfile>>(
            [new SessionProfile(ProfileLabel, new ClaudeConfig("/home/someone/.claude"))]));
        return profiles;
    }

    private static ISharedProjectSource _Source(IReadOnlyList<SharedProject> projects)
    {
        var source = Substitute.For<ISharedProjectSource>();
        source.Key.Returns("depot");
        source.SourceName.Returns("Depot — Work");
        source.ListAsync(Arg.Any<CancellationToken>()).Returns(SharedProjectListResult.Success(projects));
        return source;
    }

    private static CockpitViewModel _NewCockpit(ProjectsViewModel projects)
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
            projects: projects);
    }

    private sealed class _FakeRegistry(IReadOnlyList<ISharedProjectSource> initialSources) : ISharedProjectSourceRegistry
    {
        private readonly List<ISharedProjectSource> _sources = [.. initialSources];

        public IReadOnlyList<ISharedProjectSource> Sources => _sources;

        public event Action<ISharedProjectSource>? Registered;

        public bool Register(ISharedProjectSource source)
        {
            _sources.Add(source);
            Registered?.Invoke(source);
            return true;
        }

        public void Remove(string key) => _sources.RemoveAll(existing => existing.Key == key);
    }
}
