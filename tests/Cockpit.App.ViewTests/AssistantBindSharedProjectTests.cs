using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Layout;
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
/// AC-798: <c>AssistantAgentGateway.BindSharedProjectAsync</c> — the "Add to my projects…" step without the window.
/// What it produces has to be the dialog's own project rather than a second assembly of the same fields, and the
/// three things the shared definition deliberately does not carry (folder, profile, machine-specific resource rows)
/// have to be asked for rather than filled in.
/// </summary>
[Collection("avalonia")]
public class AssistantBindSharedProjectTests : IDisposable
{
    private const string ProfileLabel = "Zyra";

    private readonly string _folder = Directory.CreateTempSubdirectory("ac798-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    // ── Criterion 1 and 3: it binds, and what it stores is what the dialog would have stored ───────────────────

    [Fact]
    public async Task Binds_TheStoredProjectIsTheOneTheDialogWouldHaveBuilt()
    {
        var binding = new SharedProjectBinding("Handbook")
        {
            Description = "The team handbook",
            BehaviorPrompt = "Write in Dutch.",
            IsolateInWorktreeByDefault = true,
            EnabledMcpServerNames = ["depot"],
        };
        var (gateway, projects, store) = _Build(binding);

        var result = await gateway.BindSharedProjectAsync("depot:handbook", _folder, ProfileLabel);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("Handbook", result.Name);
        Assert.Equal("Depot — Work", result.SourceName);
        Assert.Equal(_folder, result.SourceDirectory);

        // The comparison that makes this criterion 3 rather than a re-statement of the composition: the expected
        // project is built by running the dialog's own view model over the same binding and the same two answers.
        var expected = await _WhatTheDialogWouldBuildAsync(binding);
        var stored = Assert.Single(projects.Drawn().Projects);
        Assert.Equal(expected.Name, stored.Name);
        Assert.Equal(expected.Description, stored.Description);
        Assert.Equal(expected.SourceDirectory, stored.SourceDirectory);
        Assert.Equal(expected.DefaultProfileLabel, stored.DefaultProfileLabel);
        Assert.Equal(expected.BehaviorPrompt, stored.BehaviorPrompt);
        Assert.Equal(expected.IsolateInWorktreeByDefault, stored.IsolateInWorktreeByDefault);
        Assert.Equal(expected.McpOverlay.EnabledServerNames, stored.McpOverlay.EnabledServerNames);
        Assert.Equal(expected.SharedSourceName, stored.SharedSourceName);
        Assert.Equal(expected.MemoryRef, stored.MemoryRef);
        Assert.Equal(stored.Id, result.ProjectId);

        // Criterion 4, the half that is demonstrable from out here: the project reached the store through the
        // projects view model's own persisting — one normalised save of the whole settings, the same call the
        // dialog route makes — rather than a write this gateway composed itself.
        await store.Received(1).SaveAsync(Arg.Is<ProjectSettings>(settings =>
            settings.Projects.Count == 1 && settings.Projects[0].Name == "Handbook"));
    }

    [Fact]
    public async Task WithMachineSpecificResourceRowsAndNoReferences_IsRefused_WithEveryRowNamedInOrder()
    {
        var (gateway, projects, _) = _Build(new SharedProjectBinding("Handbook")
        {
            Resources =
            [
                new SharedProjectBindingResource("Instructions", string.Empty) { Label = "Onboarding runbook" },
                new SharedProjectBindingResource("Reference", string.Empty) { Label = "Test dataset" },
            ],
        });

        var result = await gateway.BindSharedProjectAsync("depot:handbook", _folder, ProfileLabel);

        Assert.False(result.Ok);
        Assert.Contains("1. Onboarding runbook", result.Error);
        Assert.Contains("2. Test dataset", result.Error);

        // The refusal is the whole point: a blank row is dropped on save, so binding anyway would have produced a
        // project quietly missing a resource nobody was told about.
        Assert.Empty(projects.Drawn().Projects);
    }

    [Fact]
    public async Task AProjectAlreadyAddedHere_IsRefusedRatherThanAddedTwice()
    {
        // The dialog is protected by the row disappearing off the list; this door has no list to disappear from.
        var bound = new Project("local-1", "Handbook")
        {
            Resources = [new ProjectResource("depot:handbook", ProjectResourceRole.Memory)],
        };
        var (gateway, projects, _) = _Build(
            new SharedProjectBinding("Handbook"), ProjectSettings.Empty with { Projects = [bound] });

        var result = await gateway.BindSharedProjectAsync("depot:handbook", _folder, ProfileLabel);

        Assert.False(result.Ok);
        Assert.Contains("already added", result.Error);
        Assert.Single(projects.Drawn().Projects);
    }

    [Fact]
    public async Task AProjectHiddenOnThisMachine_IsRefused_RatherThanBoundPastTheOperatorsChoice()
    {
        // list_shared_projects already leaves a hidden project out, and the Projects page has no card to click for
        // one — so a door that bound it anyway would be the single way around a choice the operator made.
        var (gateway, projects, _) = _Build(
            new SharedProjectBinding("Handbook"),
            ProjectSettings.Empty with { HiddenSharedProjectIds = ["depot:handbook"] });

        var result = await gateway.BindSharedProjectAsync("depot:handbook", _folder, ProfileLabel);

        Assert.False(result.Ok);
        Assert.Contains("hidden", result.Error);
        Assert.Empty(projects.Drawn().Projects);
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The same binding run through the dialog's own view model and its two answers — criterion 3's yardstick.</summary>
    private async Task<Project> _WhatTheDialogWouldBuildAsync(SharedProjectBinding binding)
    {
        var (viewModel, _) = await SharedProjectBindingDialogViewModel.CreateAsync(
            "depot:handbook", "Depot — Work", _Source(SharedProjectBindingResult.Success(binding)), _Profiles());
        Assert.NotNull(viewModel);
        viewModel.ApplyPickedDirectory(_folder);
        viewModel.SelectedProfileLabel = ProfileLabel;
        return viewModel.ToProject();
    }

    private (AssistantAgentGateway Gateway, ProjectsViewModel Projects, IProjectStore Store) _Build(
        SharedProjectBinding binding, ProjectSettings? settings = null) =>
        _Build(SharedProjectBindingResult.Success(binding), settings);

    private (AssistantAgentGateway Gateway, ProjectsViewModel Projects, IProjectStore Store) _Build(
        SharedProjectBindingResult prepared, ProjectSettings? settings = null)
    {
        var store = Substitute.For<IProjectStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(settings ?? ProjectSettings.Empty);

        var registry = new _FakeRegistry([_Source(prepared)]);
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
            projects: catalog));

        return (gateway, projects, store);
    }

    private static ISessionProfileStore _Profiles()
    {
        var profiles = Substitute.For<ISessionProfileStore>();
        profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SessionProfile>>(
            [new SessionProfile(ProfileLabel, new ClaudeConfig("/home/someone/.claude"))]));
        return profiles;
    }

    private static ISharedProjectSource _Source(SharedProjectBindingResult prepared)
    {
        var source = Substitute.For<ISharedProjectSource>();
        source.Key.Returns("depot");
        source.SourceName.Returns("Depot — Work");
        source.ListAsync(Arg.Any<CancellationToken>()).Returns(
            SharedProjectListResult.Success([new SharedProject("depot:handbook", "Handbook")]));
        source.PrepareBindingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(prepared);
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
