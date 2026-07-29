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

    // AC-504: the project's own Memory-role reference(s), reduced to their scheme, reach a plugin provider — not
    // just projectId — so a plugin whose servers differ by *which* of its own connections a project points at
    // (Depot) can tell them apart.
    [Fact]
    public async Task GetServersForProjectAsync_ProjectHasAMemoryRowWithAScheme_PassesThatSchemeToPluginProviders()
    {
        var provider = new _SchemeCapturingPluginMcpProvider();
        var project = new Project("project-a", "Project") { MemoryRef = "depot.wispslate:my-slug" };
        var catalog = _CatalogWith(provider, project);

        _ = await catalog.GetServersForProjectAsync("project-a");

        Assert.Equal(["depot.wispslate"], provider.LastSchemes);
    }

    [Fact]
    public async Task GetServersForProjectAsync_ProjectHasTwoMemoryRows_PassesBothSchemes()
    {
        var provider = new _SchemeCapturingPluginMcpProvider();
        var project = new Project("project-a", "Project")
        {
            Resources =
            [
                new ProjectResource("depot:my-slug", ProjectResourceRole.Memory),
                new ProjectResource("depot.wispslate:other-slug", ProjectResourceRole.Memory),
            ],
        };
        var catalog = _CatalogWith(provider, project);

        _ = await catalog.GetServersForProjectAsync("project-a");

        Assert.Equal(["depot", "depot.wispslate"], provider.LastSchemes);
    }

    // AC-504 criterion 7 (regression): a plain folder path has no scheme for TryParse to find, so it never reaches
    // a plugin as one — the gap that would otherwise let a project with only a Folder memory row look, to a plugin,
    // like it named that plugin's own scheme.
    [Fact]
    public async Task GetServersForProjectAsync_ProjectHasAFolderMemoryRow_PassesNoSchemeToPluginProviders()
    {
        var provider = new _SchemeCapturingPluginMcpProvider();
        var project = new Project("project-a", "Project") { MemoryRef = @"C:\Users\raymond\memory" };
        var catalog = _CatalogWith(provider, project);

        _ = await catalog.GetServersForProjectAsync("project-a");

        Assert.Empty(provider.LastSchemes!);
    }

    [Fact]
    public async Task GetServersForProjectAsync_ProjectHasNoMemoryRow_PassesNoSchemeToPluginProviders()
    {
        var provider = new _SchemeCapturingPluginMcpProvider();
        var catalog = _CatalogWith(provider, new Project("project-a", "Project"));

        _ = await catalog.GetServersForProjectAsync("project-a");

        Assert.Empty(provider.LastSchemes!);
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

    // A row of a different role (Instructions, Reference) must not contribute a scheme even when its reference
    // happens to parse as one — only a Memory row says "this is where the project's memory lives".
    [Fact]
    public async Task GetServersForProjectAsync_NonMemoryRoleRowWithADepotShapedReference_PassesNoScheme()
    {
        var provider = new _SchemeCapturingPluginMcpProvider();
        var project = new Project("project-a", "Project")
        {
            Resources = [new ProjectResource("depot:my-slug", ProjectResourceRole.Instructions)],
        };
        var catalog = _CatalogWith(provider, project);

        _ = await catalog.GetServersForProjectAsync("project-a");

        Assert.Empty(provider.LastSchemes!);
    }

    // A hand-edited cockpit.json could carry surrounding whitespace on a stored reference; the scheme resolved here
    // must match what SessionStartDefaults.Resolve resolves for the very same reference, which trims before parsing.
    [Fact]
    public async Task GetServersForProjectAsync_ReferenceHasSurroundingWhitespace_StillResolvesTheScheme()
    {
        var provider = new _SchemeCapturingPluginMcpProvider();
        var project = new Project("project-a", "Project")
        {
            Resources = [new ProjectResource("  depot:my-slug  ", ProjectResourceRole.Memory)],
        };
        var catalog = _CatalogWith(provider, project);

        _ = await catalog.GetServersForProjectAsync("project-a");

        Assert.Equal(["depot"], provider.LastSchemes);
    }

    private static McpServerCatalog _CatalogWith(IPluginMcpProvider provider, string knownProjectId) =>
        _CatalogWith(provider, new Project(knownProjectId, "Project"));

    private static McpServerCatalog _CatalogWith(IPluginMcpProvider provider, Project knownProject)
    {
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>());

        var projectStore = Substitute.For<IProjectStore>();
        projectStore.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(ProjectSettings.Empty.WithProject(knownProject));

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
