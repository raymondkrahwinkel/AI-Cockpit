using Avalonia.Threading;
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
using Cockpit.Infrastructure.Projects;
using Cockpit.Infrastructure.Assistant;
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
        // the second job while the first is still awaiting inside the save — the actual interleaving a lost
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

        var projects = TestProjects.ViewModel(store, dialogs: null);
        await Dispatcher.UIThread.InvokeAsync(() => projects.LoadAsync());

        var assistantProject = Project.Create("Assistant project") with { SourceDirectories = [new(_folder)] };
        var dialogProject = Project.Create("Dialog project") with { SourceDirectories = [new(_folder)] };

        var assistantTask = Dispatcher.UIThread.InvokeAsync(() => projects.AddNewProjectAsync(assistantProject));
        var dialogTask = Dispatcher.UIThread.InvokeAsync(() => projects.AddNewProjectAsync(dialogProject));

        // The first job is suspended inside the store's save while the second has already started. AC-1435: the
        // project catalog serialises its writes, so the second reads the settings only after the first stored them.
        await firstSaveReached.Task;
        Assert.False(secondSaveReached.Task.IsCompleted);

        releaseSaves.SetResult();
        await Task.WhenAll(assistantTask, dialogTask);

        Assert.Equal(2, projects.Drawn().Projects.Count);
        Assert.Contains(projects.Drawn().Projects, project => project.Name == "Assistant project");
        Assert.Contains(projects.Drawn().Projects, project => project.Name == "Dialog project");
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
        Assert.Empty(projects.Drawn().Projects);
    }

    // ── sourceDirectory: full path, and it has to exist ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ARelativeSourceDirectory_IsRefused()
    {
        var (gateway, projects, _) = _Build();

        var result = await gateway.CreateProjectAsync("Invoices", sourceDirectory: "relative/path");

        Assert.False(result.Ok);
        Assert.Contains("relative path", result.Error);
        Assert.Empty(projects.Drawn().Projects);
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
        Assert.Empty(projects.Drawn().Projects);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────────────────────────────────────

    private (AssistantAgentGateway Gateway, ProjectsViewModel Projects, IProjectStore Store) _Build(
        IReadOnlyList<SharedProject>? sharedProjects = null,
        IReadOnlyList<ProjectFieldRegistration>? pluginFields = null,
        ProjectSettings? settings = null)
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

        var catalog = TestProjects.Catalog(store, registry);
        var projects = TestProjects.ViewModel(catalog, dialogs: null);
        Dispatcher.UIThread.Invoke(() => projects.LoadAsync()).GetAwaiter().GetResult();

        var sessions = new SessionRegistry();
        var gateway = Dispatcher.UIThread.Invoke(() => AssistantAgentGatewayGraph.Over(
            _NewCockpit(projects, sessions),
            sessions,
            _Profiles(),
            Substitute.For<IAssistantSpawnAuditLog>(),
            Substitute.For<IWorkspaceAgentGateway>(),
            Substitute.For<IAgentMessageInbox>(),
            Substitute.For<IAgentNotifyAuditLog>(),
            Substitute.For<IPluginProviderRegistry>(),
            new SessionWatcher(Substitute.For<IAgentMessageInbox>()),
            Substitute.For<IAssistantSessionHost>(),
            worktreeManager: null,
            sharedProjectSources: registry,
            mcpServerCatalog: _McpCatalog(),
            projectFields: fieldRegistry,
            projects: catalog));

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

    private static CockpitViewModel _NewCockpit(ProjectsViewModel projects, SessionRegistry sessions)
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
            projects: projects,
            sessionRegistry: sessions);
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
