using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.App.Plugins;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Mcp;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Mcp;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// <see cref="McpServerCatalog.GetServersForProjectAsync"/> passing <c>projectId</c> to each plugin's own
/// <see cref="IPluginMcpProvider.GetMcpServers(string?)"/> (AC-500) — not only applying the project's
/// <see cref="Core.Projects.ProjectMcpOverlay"/> to an already-unscoped merge, which could only ever remove a
/// plugin's server, never add one that belongs to just one project.
/// </summary>
public class McpServerCatalogProjectScopingTests
{
    [Fact]
    public async Task GetServersForProjectAsync_APluginServerForOneProject_AppearsOnlyThere()
    {
        var catalog = _CatalogWith(new _PerProjectPluginMcpProvider(), "project-a");

        var forA = await catalog.GetServersForProjectAsync("project-a");
        var forB = await catalog.GetServersForProjectAsync("project-b");
        var unscoped = await catalog.GetServersAsync();

        Assert.Contains(forA, server => server.Name == "project-a-server");
        Assert.DoesNotContain(forB, server => server.Name == "project-a-server");
        Assert.DoesNotContain(unscoped, server => server.Name == "project-a-server");
    }

    // Acceptance criterion 5: an existing IPluginMcpProvider (YouTrack e.a.) that never overrode the new
    // GetMcpServers(string?) overload keeps contributing the same servers to every session — with a project, a
    // different project, or none — via the default method's fallback to GetMcpServers().
    [Fact]
    public async Task GetServersForProjectAsync_AProviderThatNeverOverrodeTheProjectOverload_StaysGlobalEverywhere()
    {
        var catalog = _CatalogWith(new _ProjectAgnosticPluginMcpProvider(), "project-a");

        var forA = await catalog.GetServersForProjectAsync("project-a");
        var forB = await catalog.GetServersForProjectAsync("project-b");
        var unscoped = await catalog.GetServersAsync();

        Assert.Contains(forA, server => server.Name == "global-server");
        Assert.Contains(forB, server => server.Name == "global-server");
        Assert.Contains(unscoped, server => server.Name == "global-server");
    }

    private static McpServerCatalog _CatalogWith(IPluginMcpProvider provider, string knownProjectId)
    {
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>());

        var projectStore = Substitute.For<IProjectStore>();
        projectStore.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(ProjectSettings.Empty.WithProject(new Project(knownProjectId, "Project")));

        return new McpServerCatalog(store, projectStore, [provider], [], NullLogger<McpServerCatalog>.Instance);
    }

    private sealed class _PerProjectPluginMcpProvider : IPluginMcpProvider
    {
        public IReadOnlyList<McpServerContribution> GetMcpServers() => [];

        public IReadOnlyList<McpServerContribution> GetMcpServers(string? projectId) =>
            projectId == "project-a" ? [new McpServerContribution("project-a-server", "https://a.example/mcp")] : [];
    }

    private sealed class _ProjectAgnosticPluginMcpProvider : IPluginMcpProvider
    {
        public IReadOnlyList<McpServerContribution> GetMcpServers() =>
            [new McpServerContribution("global-server", "https://global.example/mcp")];
    }
}
