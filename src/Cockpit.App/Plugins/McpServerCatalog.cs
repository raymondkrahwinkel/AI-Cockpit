using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
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
internal sealed class McpServerCatalog(IMcpServerStore store, IEnumerable<IPluginMcpProvider> pluginProviders, ILogger<McpServerCatalog> logger)
    : IMcpServerCatalog, ISingletonService
{
    public async Task<IReadOnlyList<McpServerConfig>> GetServersAsync(CancellationToken cancellationToken = default)
    {
        var registry = await store.LoadAsync(cancellationToken).ConfigureAwait(false);

        var pluginServers = pluginProviders
            .SelectMany(_ServersOf)
            .Select(PluginMcpMapping.ToServerConfig)
            .ToList();

        return Merge(registry, pluginServers);
    }

    /// <summary>
    /// The registry with the plugin-owned servers merged in: registry entries first, then every plugin server. A
    /// plugin owns its own names, so its live answer wins over a registry entry of the same name — the case that
    /// arises for one start after upgrade, before the pre-AC-11 push entries are reconciled away. Pulled out so
    /// the merge is unit-testable without standing up a PluginManager.
    /// </summary>
    internal static IReadOnlyList<McpServerConfig> Merge(IReadOnlyList<McpServerConfig> registry, IReadOnlyList<McpServerConfig> pluginServers)
    {
        var pluginNames = pluginServers.Select(server => server.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. registry.Where(server => !pluginNames.Contains(server.Name)), .. pluginServers];
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
}
