using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Projects;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Projects;

public sealed class ProjectCatalogPublicationTests
{
    public static TheoryData<SharedProjectReadState, bool, bool, int, bool> Reads => new()
    {
        { SharedProjectReadState.Absent, true, true, 1, false },
        { SharedProjectReadState.Absent, true, false, 1, true },
        { SharedProjectReadState.Timeout, true, true, 0, true },
        { SharedProjectReadState.Unreadable, false, true, 0, true },
    };

    [Theory]
    [MemberData(nameof(Reads))]
    public async Task Refresh_BackfillsOnlyConfirmedAbsence_AndSurfacesFailure(
        SharedProjectReadState state, bool reachable, bool writable, int writes, bool hasError)
    {
        var project = new Project("local", "Local")
        {
            Resources = [new ProjectResource("memory:acme/local", ProjectResourceRole.Memory)],
        };
        var store = Substitute.For<IProjectStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new ProjectSettings { Projects = [project] });
        var source = Substitute.For<ISharedProjectSource>();
        source.Key.Returns("memory");
        source.SourceName.Returns("Memory");
        source.CanPublish.Returns(true);
        source.ListAsync(Arg.Any<CancellationToken>()).Returns(new SharedProjectListResult(reachable, [], reachable ? null : "Offline")
        {
            ProjectReadStates = [new SharedProjectReadStatus("memory:acme/local", "Local", "Editor", state)],
        });
        source.PublishAsync(Arg.Any<string>(), Arg.Any<SharedProjectPublishDefinition>(), Arg.Any<CancellationToken>())
            .Returns(writable ? SharedProjectPublishResult.Success("memory:acme/local") : SharedProjectPublishResult.PermissionDenied("Permission denied"));
        var registry = new SharedProjectSourceRegistry();
        registry.Register(source);
        var catalog = new ProjectCatalog(store, new ProjectOwnershipRegistry(), registry);

        await catalog.RefreshSharedProjectsAsync();

        await source.Received(writes).PublishAsync("memory:acme/local",
            Arg.Is<SharedProjectPublishDefinition>(definition => definition.Name == "Local"), Arg.Any<CancellationToken>());
        Assert.Equal(hasError, catalog.Current.PublicationErrors.ContainsKey("local"));

        source.ListAsync(Arg.Any<CancellationToken>()).Returns(SharedProjectListResult.Success([]) with
        {
            ProjectReadStates = [new SharedProjectReadStatus("memory:acme/local", "Local", "Editor", SharedProjectReadState.Absent)],
        });
        source.PublishAsync(Arg.Any<string>(), Arg.Any<SharedProjectPublishDefinition>(), Arg.Any<CancellationToken>())
            .Returns(SharedProjectPublishResult.Success("memory:acme/local"));

        await catalog.RefreshSharedProjectsAsync();

        await source.Received(writes + 1).PublishAsync("memory:acme/local", Arg.Any<SharedProjectPublishDefinition>(), Arg.Any<CancellationToken>());
        Assert.Empty(catalog.Current.PublicationErrors);
    }
}
