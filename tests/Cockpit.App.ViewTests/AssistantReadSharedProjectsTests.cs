using Avalonia.Threading;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.Projects;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-797: <c>AssistantReadGateway.ListSharedProjectsAsync</c> — one failed source must not cost another
/// source's rows, and a project already bound or hidden here must not be offered again. Built through
/// <c>ProjectsViewModel</c>'s real settings, the same route <c>ProjectsWorkspaceSharedProjectsTests</c> uses,
/// since the filter this tool shares with the Projects workspace (<c>SharedProjectVisibilityFilterIds</c>) reads
/// from there rather than from the plain observable lists.
/// </summary>
[Collection("avalonia")]
public class AssistantReadSharedProjectsTests
{
    [Fact]
    public async Task ListSharedProjectsAsync_OneFailedSourceDoesNotCostTheOthersRows()
    {
        var working = new _FakeSharedProjectSource(
            "Depot — Work", SharedProjectListResult.Success([new SharedProject("depot:proj-1", "Marketing site")]));
        var broken = new _FakeSharedProjectSource("Depot — Personal", exception: new InvalidOperationException("not signed in"));
        var gateway = await _BuildAsync([working, broken], ProjectSettings.Empty);

        var sources = await gateway.ListSharedProjectsAsync();

        var workRow = Assert.Single(sources, source => source.SourceName == "Depot — Work");
        Assert.True(workRow.Succeeded);
        Assert.Equal("proj-1", Assert.Single(workRow.Projects).Id.Split(':')[^1]);

        var personalRow = Assert.Single(sources, source => source.SourceName == "Depot — Personal");
        Assert.False(personalRow.Succeeded);
        Assert.Contains("not signed in", personalRow.Error);
        Assert.Empty(personalRow.Projects);
    }

    [Fact]
    public async Task ListSharedProjectsAsync_LeavesOutAProjectAlreadyBoundHere()
    {
        var source = new _FakeSharedProjectSource("Depot — Work", SharedProjectListResult.Success(
        [
            new SharedProject("depot:proj-1", "Marketing site"),
            new SharedProject("depot:proj-2", "Internal wiki"),
        ]));
        var bound = new Project("local-1", "Marketing site")
        {
            Resources = [new ProjectResource("depot:proj-1", ProjectResourceRole.Memory)],
        };
        var gateway = await _BuildAsync([source], ProjectSettings.Empty with { Projects = [bound] });

        var sources = await gateway.ListSharedProjectsAsync();

        var project = Assert.Single(Assert.Single(sources).Projects);
        Assert.Equal("depot:proj-2", project.Id);
    }

    [Fact]
    public async Task ListSharedProjectsAsync_LeavesOutAProjectHiddenOnThisMachine()
    {
        var source = new _FakeSharedProjectSource("Depot — Work", SharedProjectListResult.Success(
        [
            new SharedProject("depot:proj-1", "Marketing site"),
            new SharedProject("depot:proj-2", "Internal wiki"),
        ]));
        var gateway = await _BuildAsync([source], ProjectSettings.Empty with { HiddenSharedProjectIds = ["depot:proj-1"] });

        var sources = await gateway.ListSharedProjectsAsync();

        var project = Assert.Single(Assert.Single(sources).Projects);
        Assert.Equal("depot:proj-2", project.Id);
    }

    private static async Task<AssistantReadGateway> _BuildAsync(IReadOnlyList<ISharedProjectSource> sources, ProjectSettings settings)
    {
        var store = Substitute.For<IProjectStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(settings);
        var projects = new ProjectsViewModel(store, dialogs: null);
        await projects.LoadAsync();

        return Dispatcher.UIThread.Invoke(() =>
            new AssistantReadGateway(_NewCockpit(projects), new _FakeSharedProjectSourceRegistry(sources)));
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

    private sealed class _FakeSharedProjectSourceRegistry(IReadOnlyList<ISharedProjectSource> initialSources) : ISharedProjectSourceRegistry
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

    private sealed class _FakeSharedProjectSource(string sourceName, SharedProjectListResult? result = null, Exception? exception = null)
        : ISharedProjectSource
    {
        public string Key => sourceName;

        public string SourceName => sourceName;

        public Task<SharedProjectListResult> ListAsync(CancellationToken cancellationToken) =>
            exception is null ? Task.FromResult(result!) : throw exception;

        public bool CanPublish => false;

        public Task<SharedProjectBindingResult> PrepareBindingAsync(string id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SharedProjectWriteBackResult> WriteBackAsync(string id, SharedProjectDefinitionEdit edit, string baseChecksum, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SharedProjectPublishTargetListResult> ListPublishTargetsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SharedProjectPublishResult> PublishAsync(string targetId, SharedProjectPublishDefinition definition, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
