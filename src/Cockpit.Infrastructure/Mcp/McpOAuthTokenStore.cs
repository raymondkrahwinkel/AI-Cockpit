using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Core.Secrets;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Mcp;

/// <summary>
/// Persists MCP OAuth tokens under the <c>mcpOAuthTokens</c> section of <c>cockpit.json</c> (same
/// read-modify-write-the-whole-file pattern as the other section stores, so siblings stay intact).
/// </summary>
internal sealed class McpOAuthTokenStore : IMcpOAuthTokenStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public McpOAuthTokenStore()
        : this(CockpitConfigPath.Default)
    {
    }

    /// <summary>Test seam: point the store at an arbitrary config file path, and at a key holder that is not the process-wide one.</summary>
    internal McpOAuthTokenStore(string configFilePath, ISecretKeyHolder? keyHolder = null)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath, keyHolder);
    }

    public async Task<McpOAuthToken?> GetAsync(string serverId, CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.McpOAuthTokens?.FirstOrDefault(entry => Matches(entry, serverId))?.ToDomain();
    }

    public Task SaveAsync(string serverId, string serverName, McpOAuthToken token, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file =>
            {
                file.McpOAuthTokens.RemoveAll(entry => Matches(entry, serverId));
                file.McpOAuthTokens.Add(McpOAuthTokenEntry.FromDomain(serverId, serverName, token));
            },
            cancellationToken);

    public Task RemoveAsync(string serverId, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.McpOAuthTokens.RemoveAll(entry => Matches(entry, serverId)),
            cancellationToken);

    public async Task AdoptLegacyEntriesAsync(IReadOnlyDictionary<string, string> idsByServerName, CancellationToken cancellationToken = default)
    {
        // Read first and leave without writing when there is nothing to adopt, which is every start after the first
        // one: this runs on every launch, and rewriting cockpit.json each time to change nothing is the kind of
        // needless churn the registry's own removal path already refuses to do.
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (configFile?.McpOAuthTokens is null
            || !configFile.McpOAuthTokens.Any(entry => string.IsNullOrEmpty(entry.ServerId)))
        {
            return;
        }

        await _configFile.UpdateAsync(
            file =>
            {
                // Whatever is already filed under an id keeps it — a real sign-in outranks a guess made from a name,
                // and this must never overwrite one. Recomputed against the live list rather than the snapshot, so
                // two legacy entries claiming the same id cannot both take it either.
                foreach (var entry in file.McpOAuthTokens.Where(entry => string.IsNullOrEmpty(entry.ServerId)))
                {
                    if (!idsByServerName.TryGetValue(entry.ServerName.Trim(), out var serverId)
                        || string.Equals(serverId, McpServerIdentity.LegacyIdFor(entry.ServerName), StringComparison.Ordinal)
                        || file.McpOAuthTokens.Any(other => string.Equals(other.ServerId, serverId, StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    entry.ServerId = serverId;
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether <paramref name="entry"/> is the token held for <paramref name="serverId"/> (AC-403).
    /// <para>
    /// Two ways in, and the second one is the whole migration. An entry written since this id exists carries it,
    /// and is matched on that alone. An entry an older build wrote has no id and was filed under the server's
    /// name — so it answers to the id that name derives to, which is precisely the id
    /// <see cref="McpServerConfig.IdentityKey"/> hands back for a server that has not been given one of its own.
    /// Nothing has to be rewritten for that to hold.
    /// </para>
    /// <para>
    /// ⚠️ What is deliberately <em>not</em> here is a fallback onto the server's <em>current</em> name. That is the
    /// defect this ticket is about: two servers on one host that swap names would each adopt the other's token and
    /// present a bearer to an endpoint it was never issued for. A derived legacy id is fixed at the name the row
    /// carried when its id was first needed, and travels with the row from then on; the current name never enters
    /// the comparison.
    /// </para>
    /// </summary>
    internal static bool Matches(McpOAuthTokenEntry entry, string serverId) =>
        !string.IsNullOrEmpty(entry.ServerId)
            ? string.Equals(entry.ServerId, serverId, StringComparison.Ordinal)
            : string.Equals(McpServerIdentity.LegacyIdFor(entry.ServerName), serverId, StringComparison.Ordinal);
}
