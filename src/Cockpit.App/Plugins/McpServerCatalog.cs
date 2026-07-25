using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Mcp;
using Cockpit.Plugins.Abstractions.Mcp;

namespace Cockpit.App.Plugins;

/// <summary>
/// The effective MCP-server set a session sees (#26, AC-11): the user-managed registry merged with what each
/// active plugin provides for itself. The providers are the plugins that registered themselves as
/// <see cref="IPluginMcpProvider"/> in their <c>ConfigureServices</c>, injected here as a set. This is what the
/// fan-out and the New-session checklist read, so plugin-owned servers are offered and per-session uncheckable
/// alongside registry ones — while the MCP-servers manager keeps reading <see cref="IMcpServerStore"/> directly
/// and so never lists them.
/// </summary>
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
        var servers = await GetServersAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(projectId))
        {
            return servers;
        }

        var projects = await projectStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        return projects.Find(projectId)?.McpOverlay.ApplyTo(servers) ?? servers;
    }

    public async Task<IReadOnlyList<McpServerConfig>> GetServersAsync(CancellationToken cancellationToken = default)
    {
        var registry = await store.LoadAsync(cancellationToken).ConfigureAwait(false);

        // The cockpit's own loopback endpoints (AC-40): answered live, never in the store, so the manager never
        // lists them while the session fan-out still sees them.
        var internalServers = internalProviders.SelectMany(_ServersOf).ToList();

        var pluginServers = pluginProviders
            .SelectMany(_ServersOf)
            .Select(PluginMcpMapping.ToServerConfig)
            .ToList();

        return Merge(registry, [.. internalServers, .. pluginServers]);
    }

    /// <summary>
    /// The registry with the cockpit-hosted and plugin-owned servers merged in: registry entries first, then the
    /// provided ones. A provider owns its own names, so its live answer wins over a registry entry of the same name
    /// — the case that arises for one start after upgrade, before the older push entries are reconciled away. Two
    /// providers claiming the same name is not expected (the cockpit's own endpoint names are disjoint from the
    /// plugins'), but if it ever happens the first one caller order gives — a cockpit-hosted endpoint ahead of a
    /// plugin's — wins, rather than a session seeing the same server twice. Pulled out so the merge is unit-testable
    /// without standing up a PluginManager.
    /// </summary>
    internal static IReadOnlyList<McpServerConfig> Merge(IReadOnlyList<McpServerConfig> registry, IReadOnlyList<McpServerConfig> providedServers)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var provided = providedServers.Where(server => seen.Add(server.Name)).ToList();
        return [.. registry.Where(server => !seen.Contains(server.Name)), .. provided];
    }

    // A plugin that throws while listing its servers must not break session start for everyone else — its servers
    // are simply absent for this assembly, and the failure is logged.
    private IReadOnlyList<McpServerContribution> _ServersOf(IPluginMcpProvider provider)
    {
        try
        {
            return provider.GetMcpServers();
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
