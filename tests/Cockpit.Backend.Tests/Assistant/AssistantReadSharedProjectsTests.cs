using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Projects;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Projects;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

namespace Cockpit.Backend.Tests.Assistant;

/// <summary>
/// AC-797/AC-1374: <c>AssistantReadGateway.ListSharedProjectsAsync</c> — one failed source must not cost another
/// source's rows, and a project already bound or hidden here must not be offered again. Moved off the VM entirely
/// (AC-1374): the moved gateway reads <see cref="IProjectStore"/> directly, no <c>ProjectsViewModel</c> needed.
/// </summary>
public class AssistantReadSharedProjectsTests
{
    [Fact]
    public async Task ListSharedProjectsAsync_OneFailedSourceDoesNotCostTheOthersRows()
    {
        var working = new _FakeSharedProjectSource(
            "Depot — Work", SharedProjectListResult.Success([new SharedProject("depot:proj-1", "Marketing site")]));
        var broken = new _FakeSharedProjectSource("Depot — Personal", exception: new InvalidOperationException("not signed in"));
        var gateway = _Build([working, broken], ProjectSettings.Empty);

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
        var gateway = _Build([source], ProjectSettings.Empty with { Projects = [bound] });

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
        var gateway = _Build([source], ProjectSettings.Empty with { HiddenSharedProjectIds = ["depot:proj-1"] });

        var sources = await gateway.ListSharedProjectsAsync();

        var project = Assert.Single(Assert.Single(sources).Projects);
        Assert.Equal("depot:proj-2", project.Id);
    }

    private static AssistantReadGateway _Build(IReadOnlyList<ISharedProjectSource> sources, ProjectSettings settings)
    {
        var store = Substitute.For<IProjectStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(settings);
        var workspaceStore = Substitute.For<IWorkspaceSettingsStore>();
        workspaceStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(WorkspaceSettings.Default);

        return new AssistantReadGateway(
            Substitute.For<ISessionRegistry>(), new _FakeSharedProjectSourceRegistry(sources), store, workspaceStore);
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
