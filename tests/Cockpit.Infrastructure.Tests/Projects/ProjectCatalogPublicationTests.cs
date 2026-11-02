using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Projects;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Projects;

public sealed class ProjectCatalogPublicationTests
{
    [Fact]
    public async Task Refresh_ConfirmedAbsent_PublishesOnceWithTheLocalName_WithoutPublicationErrors()
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
        source.ListAsync(Arg.Any<CancellationToken>()).Returns(SharedProjectListResult.Success([]) with
        {
            ProjectReadStates = [new SharedProjectReadStatus("memory:acme/local", "Local", "Editor", SharedProjectReadState.Absent)],
        });
        source.PublishAsync(Arg.Any<string>(), Arg.Any<SharedProjectPublishDefinition>(), Arg.Any<CancellationToken>())
            .Returns(SharedProjectPublishResult.Success("memory:acme/local"));
        var registry = new SharedProjectSourceRegistry();
        registry.Register(source);
        var catalog = new ProjectCatalog(store, new ProjectOwnershipRegistry(), registry);

        await catalog.RefreshSharedProjectsAsync();

        await source.Received(1).PublishAsync("memory:acme/local",
            Arg.Is<SharedProjectPublishDefinition>(definition => definition.Name == "Local"), Arg.Any<CancellationToken>());
        Assert.Empty(catalog.Current.PublicationErrors);
    }
}
