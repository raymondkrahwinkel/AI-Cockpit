using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Plugins;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// <see cref="CockpitHost.GetProjectMemoryRowsAsync"/> (AC-827): the read seam over a project's own
/// <see cref="ProjectResourceRole.Memory"/> rows, paneId-resolved the same way
/// <see cref="CockpitHost.GetProjectFieldValueAsync"/> already is.
/// </summary>
public class CockpitHostProjectMemoryRowsTests
{
    [Fact]
    public async Task NoLinkedProject_ReturnsEmpty()
    {
        var host = _BuildHost(projectId: null, project: null);

        Assert.Empty(await host.GetProjectMemoryRowsAsync("pane-1"));
    }

    [Fact]
    public async Task ProjectWithOneMemoryRow_ReturnsThatRow()
    {
        var project = new Project("project-a", "Project")
        {
            Resources = [new ProjectResource("depot:my-slug", ProjectResourceRole.Memory) { Label = "Depot" }],
        };
        var host = _BuildHost("project-a", project);

        var rows = await host.GetProjectMemoryRowsAsync("pane-1");

        var row = Assert.Single(rows);
        Assert.Equal("depot:my-slug", row.Reference);
        Assert.Equal("Depot", row.Label);
        Assert.True(row.ReachesSessions);
    }

    [Fact]
    public async Task ProjectWithTwoMemoryRows_ReturnsBoth()
    {
        var project = new Project("project-a", "Project")
        {
            Resources =
            [
                new ProjectResource("depot:my-slug", ProjectResourceRole.Memory),
                new ProjectResource(@"C:\memory", ProjectResourceRole.Memory),
            ],
        };
        var host = _BuildHost("project-a", project);

        Assert.Equal(2, (await host.GetProjectMemoryRowsAsync("pane-1")).Count);
    }

    [Fact]
    public async Task NonMemoryRoleRows_AreExcluded()
    {
        var project = new Project("project-a", "Project")
        {
            Resources = [new ProjectResource("docs:conventions", ProjectResourceRole.Instructions)],
        };
        var host = _BuildHost("project-a", project);

        Assert.Empty(await host.GetProjectMemoryRowsAsync("pane-1"));
    }

    // ReachesSessions is reported, not silently filtered here — that rule belongs to whichever consumer decides to
    // honor it (see the method's own remarks), unlike SessionStartDefaults.Resolve which filters it for its own use.
    [Fact]
    public async Task MemoryRowThatDoesNotReachSessions_IsStillReturned_WithTheFlagReported()
    {
        var project = new Project("project-a", "Project")
        {
            Resources = [new ProjectResource("depot:my-slug", ProjectResourceRole.Memory) { ReachesSessions = false }],
        };
        var host = _BuildHost("project-a", project);

        var row = Assert.Single(await host.GetProjectMemoryRowsAsync("pane-1"));
        Assert.False(row.ReachesSessions);
    }

    private static ICockpitHost _BuildHost(string? projectId, Project? project)
    {
        var resolver = Substitute.For<ISessionProjectResolver>();
        resolver.ProjectIdOfAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(projectId);

        var settings = project is null ? ProjectSettings.Empty : ProjectSettings.Empty.WithProject(project);
        var store = Substitute.For<IProjectStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(settings);

        var services = new ServiceCollection();
        services.AddSingleton(resolver);
        services.AddSingleton(store);
        var provider = services.BuildServiceProvider();

        return new CockpitHost(
            "test-plugin",
            "Test Plugin",
            provider,
            Substitute.For<IPluginContributionSink>(),
            Substitute.For<ICockpitActions>(),
            Substitute.For<IPluginStorage>(),
            Substitute.For<IPluginDialogHost>(),
            NullCockpitSessionObserver.Instance,
            new PluginDiagnostics());
    }
}
