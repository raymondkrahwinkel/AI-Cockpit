using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Projects;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Projects;

// AC-1758: one group per connection, so a second Depot whose projects are all bound, or that is stale, still has a place.
public sealed class ProjectCatalogSharedGroupsTests
{
    [Fact]
    public async Task EveryConnectionGetsItsOwnGroup_WhateverItsListingCameBackAs()
    {
        var bound = new Project("local", "Local")
        {
            Resources = [new ProjectResource("depot:bound", ProjectResourceRole.Memory)],
        };
        var stale = new SharedProjectListResult(
            true,
            [new SharedProject("depot:p", "P") { Organization = new SharedProjectOrganization("acme", "Acme", "Owner") }],
            "Depot is unavailable; last known list.");
        var catalog = await _LoadAsync(
            new ProjectSettings { Projects = [bound] },
            _Source("synvolution", SharedProjectListResult.Success([new SharedProject("depot:bound", "Bound")])),
            _Source("stale", stale),
            _Source("wispslate", SharedProjectListResult.Failed("Sign in to this Depot connection.")));

        var groups = catalog.Current.SharedProjectGroups;

        Assert.Equal(["synvolution", "stale", "wispslate"], groups.Select(group => group.SourceKey));
        Assert.Empty(groups[0].Projects);
        Assert.True(groups[0].AllAdded);
        Assert.Null(groups[1].Error);
        Assert.Equal("Depot is unavailable; last known list.", groups[1].Notice);
        Assert.Equal(new SharedProjectOrganizationInfo("acme", "Acme", "Owner"), Assert.Single(groups[1].Projects).Organization);
        Assert.Equal("Sign in to this Depot connection.", groups[2].Error);
    }

    private static ISharedProjectSource _Source(string key, SharedProjectListResult result)
    {
        var source = Substitute.For<ISharedProjectSource>();
        source.Key.Returns(key);
        source.SourceName.Returns(key);
        source.ListAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(result));
        return source;
    }

    private static async Task<ProjectCatalog> _LoadAsync(ProjectSettings settings, params ISharedProjectSource[] sources)
    {
        var store = Substitute.For<IProjectStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(settings));
        var registry = new SharedProjectSourceRegistry();
        foreach (var source in sources)
        {
            registry.Register(source);
        }

        var catalog = new ProjectCatalog(store, new ProjectOwnershipRegistry(), registry);
        await catalog.LoadAsync();
        await catalog.RefreshSharedProjectsAsync();
        return catalog;
    }
}
