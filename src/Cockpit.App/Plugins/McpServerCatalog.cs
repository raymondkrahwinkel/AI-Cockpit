using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Mcp;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Mcp;

namespace Cockpit.App.Plugins;

// #26, AC-11: the effective MCP-server set a session sees — the user-managed registry merged with what
// each active `IPluginMcpProvider` contributes. The MCP-servers manager still reads `IMcpServerStore`
// directly, so plugin-owned servers never appear there, only in the session fan-out and checklist.
internal sealed class McpServerCatalog(
    IMcpServerStore store,
    IProjectStore projectStore,
    IEnumerable<IPluginMcpProvider> pluginProviders,
    IEnumerable<ICockpitInternalMcpProvider> internalProviders,
    ILogger<McpServerCatalog> logger)
    : IMcpServerCatalog, ISingletonService
{
    public async Task<IReadOnlyList<McpServerConfig>> GetServersForProjectAsync(string? projectId, CancellationToken cancellationToken = default)
    {
        var registry = await store.LoadAsync(cancellationToken).ConfigureAwait(false);

        // The cockpit's own loopback endpoints (AC-40): answered live, never in the store, so the manager never
        // lists them while the session fan-out still sees them.
        var internalServers = internalProviders.SelectMany(_ServersOf).ToList();

        // Loaded once, ahead of both places this session's project matters below: which Memory-role scheme(s) it
        // carries (AC-504) and, further down, its McpOverlay. Null when there is no projectId or nothing in the
        // store answers to it — the same "no project" case the overlay step always treated as a no-op.
        var projects = await projectStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        Project? project = string.IsNullOrEmpty(projectId) ? null : projects.Find(projectId);

        // AC-504: the project's Memory-role rows reduced to the scheme each names (e.g. "depot.wispslate"),
        // parsed here in Cockpit.Core so a plugin never parses a reference across the plugin-ALC boundary.
        // AC-766: an unscoped query unions every project's schemes instead of using one project's own.
        var projectMemorySchemes = (project is not null ? (IReadOnlyList<Project>)[project] : projects.Projects)
            .SelectMany(candidate => candidate.Resources)
            .Where(resource => resource.Role == ProjectResourceRole.Memory && resource.ReachesSessions)
            .Select(resource => ProjectMemoryRef.TryParse(resource.Reference.Trim(), out var scheme, out _) ? scheme : null)
            .Where(scheme => scheme is not null)
            .Select(scheme => scheme!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // AC-500/AC-504: projectId and memory schemes reach each provider directly, so a plugin can contribute
        // a project-only server. AC-736/AC-766: `schemeless` is the same answer without those schemes, so
        // scheme-gated servers can be told apart; an unscoped query has no project, so nothing is marked linked.
        var schemeless = project is null || projectMemorySchemes.Count == 0
            ? null
            : pluginProviders
                .SelectMany(provider => _ServersOf(provider, projectId, []))
                .Select(contribution => contribution.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var pluginServers = pluginProviders
            .SelectMany(provider => _ServersOf(provider, projectId, projectMemorySchemes))
            .Select(PluginMcpMapping.ToServerConfig)
            .Select(server => schemeless?.Contains(server.Name) == false ? server with { ProjectLinked = true } : server)
            .ToList();

        var servers = Merge(registry, [.. internalServers, .. pluginServers]);
        return project?.McpOverlay.ApplyTo(servers) ?? servers;
    }

    public Task<IReadOnlyList<McpServerConfig>> GetServersAsync(CancellationToken cancellationToken = default) =>
        GetServersForProjectAsync(projectId: null, cancellationToken);

    // Merges the registry with cockpit-hosted and plugin-owned servers; a provider's live answer wins over a
    // stale registry entry of the same name. Pulled out so the merge is unit-testable without a PluginManager.
    internal static IReadOnlyList<McpServerConfig> Merge(IReadOnlyList<McpServerConfig> registry, IReadOnlyList<McpServerConfig> providedServers)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var provided = providedServers.Where(server => seen.Add(server.Name)).ToList();
        return [.. registry.Where(server => !seen.Contains(server.Name)), .. provided];
    }

    // A plugin that throws while listing servers must not break session start for everyone else; its
    // servers are just absent and the failure is logged. Null projectId (AC-500) reads as project-agnostic.
    private IReadOnlyList<McpServerContribution> _ServersOf(IPluginMcpProvider provider, string? projectId, IReadOnlyList<string> projectMemorySchemes)
    {
        try
        {
            return provider.GetMcpServers(projectId, projectMemorySchemes);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "A plugin failed to list its MCP servers; leaving them out of this session.");
            return [];
        }
    }

    private IReadOnlyList<McpServerConfig> _ServersOf(ICockpitInternalMcpProvider provider)
    {
        try
        {
            return provider.GetServers();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "A cockpit-hosted MCP source failed to list its servers; leaving them out of this session.");
            return [];
        }
    }
}
