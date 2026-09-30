using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Mcp;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Mcp;
using NSubstitute;

namespace Cockpit.Backend.Tests.Mcp;

/// <summary>
/// <see cref="McpServerCatalog.GetServersForProjectAsync"/> passing <c>projectId</c> to each plugin's own
/// <see cref="IPluginMcpProvider.GetMcpServers(string?)"/> (AC-500) — not only applying the project's
/// <see cref="Core.Projects.ProjectMcpOverlay"/> to an already-unscoped merge, which could only ever remove a
/// plugin's server, never add one that belongs to just one project.
/// </summary>
public class McpServerCatalogProjectScopingTests
{
    // AC-766: the unscoped query now carries the union of every project's schemes, so a server scoped to one
    // project's Depot connection is still offered to the assistant and project-less sessions. It is not
    // ProjectLinked there: no single project points at it in an unscoped query.
    [Fact]
    public async Task GetServersForProjectAsync_APluginServerScopedToOneProjectsScheme_IsAlsoOfferedUnscopedButNotProjectLinked()
    {
        var projectA = new Project("project-a", "Project") { MemoryRef = "depot:my-slug" };
        var projectB = new Project("project-b", "Project");
        var catalog = _CatalogWith(new _SchemeScopedPluginMcpProvider(), projectA, projectB);

        var forA = await catalog.GetServersForProjectAsync("project-a");
        var forB = await catalog.GetServersForProjectAsync("project-b");
        var unscoped = await catalog.GetServersAsync();

        Assert.True(forA.Single(server => server.Name == "depot-server").ProjectLinked);
        Assert.DoesNotContain(forB, server => server.Name == "depot-server");
        Assert.False(unscoped.Single(server => server.Name == "depot-server").ProjectLinked);
    }

    // AC-766 criterion 3: a scoped query keeps seeing only its own project's schemes — the union that now reaches
    // the unscoped query must not leak into a call that names a project.
    [Fact]
    public async Task GetServersForProjectAsync_Scoped_PassesOnlyThatProjectsSchemes_NotTheUnion()
    {
        var projectA = new Project("project-a", "Project") { MemoryRef = "depot:my-slug" };
        var projectB = new Project("project-b", "Project") { MemoryRef = "youtrack:my-instance" };
        var provider = new _SchemeCapturingPluginMcpProvider();
        var catalog = _CatalogWith(provider, projectA, projectB);

        _ = await catalog.GetServersForProjectAsync("project-a");

        Assert.Equal(["depot"], provider.LastSchemes);
    }

    // A row switched off (ReachesSessions = false) must not silently hand a plugin a scheme its operator turned off
    // for sessions — the same rule SessionStartDefaults.Resolve applies before building any standing-instructions
    // block from a project's Resources.
    [Fact]
    public async Task GetServersForProjectAsync_MemoryRowDoesNotReachSessions_PassesNoSchemeToPluginProviders()
    {
        var provider = new _SchemeCapturingPluginMcpProvider();
        var project = new Project("project-a", "Project")
        {
            Resources = [new ProjectResource("depot:my-slug", ProjectResourceRole.Memory) { ReachesSessions = false }],
        };
        var catalog = _CatalogWith(provider, project);

        _ = await catalog.GetServersForProjectAsync("project-a");

        Assert.Empty(provider.LastSchemes!);
    }

    private static McpServerCatalog _CatalogWith(IPluginMcpProvider provider, params Project[] knownProjects)
    {
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>());

        var settings = knownProjects.Aggregate(ProjectSettings.Empty, (current, project) => current.WithProject(project));
        var projectStore = Substitute.For<IProjectStore>();
        projectStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(settings);

        return new McpServerCatalog(store, projectStore, [provider], [], NullLogger<McpServerCatalog>.Instance);
    }

    // A plugin whose server for a given scheme is named after that scheme — a stand-in for several Depot
    // connections at once (AC-766 criterion 2).
    private sealed class _MultiSchemePluginMcpProvider : IPluginMcpProvider
    {
        public IReadOnlyList<McpServerContribution> GetMcpServers() => [];

        public IReadOnlyList<McpServerContribution> GetMcpServers(string? projectId, IReadOnlyList<string> projectMemorySchemes) =>
            [.. projectMemorySchemes.Select(scheme => new McpServerContribution($"{scheme}-server", $"https://{scheme}.example/mcp"))];
    }

    private sealed class _ProjectAgnosticPluginMcpProvider : IPluginMcpProvider
    {
        public IReadOnlyList<McpServerContribution> GetMcpServers() =>
            [new McpServerContribution("global-server", "https://global.example/mcp")];
    }

    // The shape Depot has (AC-504): a server per connection, contributed only to a project whose Memory row names
    // that connection's own scheme.
    private sealed class _SchemeScopedPluginMcpProvider : IPluginMcpProvider
    {
        public IReadOnlyList<McpServerContribution> GetMcpServers() =>
            [new McpServerContribution("depot-server", "https://depot.example/mcp")];

        public IReadOnlyList<McpServerContribution> GetMcpServers(string? projectId, IReadOnlyList<string> projectMemorySchemes) =>
            projectMemorySchemes.Contains("depot") ? GetMcpServers() : [];
    }

    private sealed class _SchemeCapturingPluginMcpProvider : IPluginMcpProvider
    {
        public IReadOnlyList<string>? LastSchemes { get; private set; }

        public IReadOnlyList<McpServerContribution> GetMcpServers() => [];

        public IReadOnlyList<McpServerContribution> GetMcpServers(string? projectId, IReadOnlyList<string> projectMemorySchemes)
        {
            LastSchemes = projectMemorySchemes;
            return [];
        }
    }
}
