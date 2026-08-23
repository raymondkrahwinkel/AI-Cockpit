using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Plugins.Abstractions.Mcp;

namespace Cockpit.App.Plugins;

// AC-403: once at startup, moves OAuth tokens an older build filed under a server's *name* onto the id it
// is known by now (needed only for a minted, not derived, id, e.g. Depot). Must only run once, before any
// rename could happen — matching by current name would let renamed servers adopt each other's token.
internal sealed class McpOAuthTokenAdoption(
    IMcpOAuthTokenStore tokenStore,
    IMcpServerStore serverStore,
    IEnumerable<IPluginMcpProvider> pluginProviders,
    ILogger<McpOAuthTokenAdoption> logger) : ISingletonService
{
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var registry = await serverStore.LoadAsync(cancellationToken).ConfigureAwait(false);

            // The registry first and the plugins after, so a registry entry wins a name two of them claim — the same
            // precedence the host's own sign-in resolution applies to that clash.
            var idsByServerName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var server in registry.Concat(pluginProviders.SelectMany(_ContributionsOf).Select(PluginMcpMapping.ToServerConfig)))
            {
                var name = server.Name.Trim();

                // Nothing to adopt for a server whose id is the one its name already derives to: the store finds
                // that token without any rewriting. Skipped here as well as in the store, so a name clash between
                // such a server and one with a minted id cannot have the derived one take the slot.
                if (name.Length == 0 || server.IdentityKey == McpServerIdentity.LegacyIdFor(name))
                {
                    continue;
                }

                idsByServerName.TryAdd(name, server.IdentityKey);
            }

            if (idsByServerName.Count == 0)
            {
                return;
            }

            await tokenStore.AdoptLegacyEntriesAsync(idsByServerName, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A migration that cannot run leaves the operator signing in again, which is a nuisance; failing the
            // launch over it would be worse. Never names a token or any part of one (Iron Law #8).
            logger.LogWarning(exception, "Could not move stored MCP sign-ins onto their servers' ids; a server whose name has changed may ask to be signed in again.");
        }
    }

    // Same defensive read the catalog does: a plugin that throws while listing its servers must not take the
    // migration down with it — its own connections simply keep their tokens filed under their names for now.
    private IReadOnlyList<McpServerContribution> _ContributionsOf(IPluginMcpProvider provider)
    {
        try
        {
            return provider.GetMcpServers();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "A plugin failed to list its MCP servers while moving stored sign-ins onto their ids; leaving its own out of this pass.");
            return [];
        }
    }
}
