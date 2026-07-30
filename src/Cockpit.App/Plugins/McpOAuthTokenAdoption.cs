using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Plugins.Abstractions.Mcp;

namespace Cockpit.App.Plugins;

/// <summary>
/// Moves the OAuth tokens an older build filed under a server's <em>name</em> onto the stable id that server is
/// known by now (AC-403), once, at startup.
/// <para>
/// Most servers need nothing: a registry entry written before ids existed answers to the id its name derives to, so
/// its token is found by that derivation alone and no file is touched. What this exists for is a server whose id is
/// its own rather than derived — a plugin connection that mints one and keeps it across renames (a Depot
/// connection). Its token was filed under a name that derivation cannot reach from the minted id, and without this
/// the operator would be told to sign in again for a credential that is sitting right there.
/// </para>
/// <para>
/// ⚠️ <b>Why this may only run here.</b> Matching a token against a server's <em>current</em> name is the very
/// mistake this ticket removes — two servers on one host that swap names would each adopt the other's token. It is
/// safe exactly once, at a moment when no rename can have happened since the tokens were written: the first launch
/// of a build that has ids, before any dialog is open and before any session starts. Called from
/// <c>App.OnFrameworkInitializationCompleted</c> right after the plugins have registered themselves, which is the
/// earliest point at which a plugin's own servers can be asked for at all.
/// </para>
/// <para>
/// A plugin that is switched off at that moment contributes nothing and so is not migrated — and does not have to
/// be: enabling, installing or updating a plugin all take effect on the next restart rather than live (a
/// non-collectible plugin cannot load into a running process), so a plugin that is on for a session was on when
/// this ran. There is no window in which one appears afterwards carrying tokens this pass never saw.
/// </para>
/// </summary>
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
