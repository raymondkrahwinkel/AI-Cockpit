using Avalonia.Threading;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-797: <c>AssistantReadGateway.ListSharedProjectsAsync</c> — one failed source must not cost another
/// source's rows, and a project already bound here must not be offered again.
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
        var gateway = Dispatcher.UIThread.Invoke(() =>
            new AssistantReadGateway(new CockpitViewModel(), new _FakeSharedProjectSourceRegistry([working, broken])));

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
        var (gateway, _) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = new CockpitViewModel();
            cockpit.Projects.Projects.Add(new Project("local-1", "Marketing site")
            {
                Resources = [new ProjectResource("depot:proj-1", ProjectResourceRole.Memory)],
            });
            return (new AssistantReadGateway(cockpit, new _FakeSharedProjectSourceRegistry([source])), cockpit);
        });

        var sources = await gateway.ListSharedProjectsAsync();

        var row = Assert.Single(sources);
        var project = Assert.Single(row.Projects);
        Assert.Equal("depot:proj-2", project.Id);
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
