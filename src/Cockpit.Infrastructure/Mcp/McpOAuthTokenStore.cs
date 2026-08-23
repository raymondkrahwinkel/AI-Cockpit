using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Core.Secrets;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Mcp;

// Persists MCP OAuth tokens under the `mcpOAuthTokens` section of `cockpit.json` (same
// read-modify-write-the-whole-file pattern as the other section stores, so siblings stay intact).
internal sealed class McpOAuthTokenStore : IMcpOAuthTokenStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public McpOAuthTokenStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path, and at a key holder that is not the process-wide one.
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
        // Read first and skip the write when nothing would move, which is every launch after migration — avoids
        // churn on a cockpit.json section the operator hand-edits.
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (configFile?.McpOAuthTokens is null
            || !configFile.McpOAuthTokens.Any(entry => _AdoptableId(configFile.McpOAuthTokens, entry, idsByServerName) is not null))
        {
            return;
        }

        await _configFile.UpdateAsync(
            file =>
            {
                // Re-decided against the update's own list, not the outer snapshot, since that read happened
                // outside the write gate. ToList() because the loop assigns into the list it walks.
                foreach (var entry in file.McpOAuthTokens.ToList())
                {
                    if (_AdoptableId(file.McpOAuthTokens, entry, idsByServerName) is { } serverId)
                    {
                        entry.ServerId = serverId;
                    }
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    // AC-403: the id `entry` should be re-keyed onto, or null to leave it alone — already has an id, no server
    // answers to its name, the id already matches its derived name, or another entry already holds that id.
    private static string? _AdoptableId(
        List<McpOAuthTokenEntry> entries,
        McpOAuthTokenEntry entry,
        IReadOnlyDictionary<string, string> idsByServerName) =>
        string.IsNullOrEmpty(entry.ServerId)
        && idsByServerName.TryGetValue(entry.ServerName.Trim(), out var serverId)
        && !string.Equals(serverId, McpServerIdentity.LegacyIdFor(entry.ServerName), StringComparison.Ordinal)
        && !entries.Any(other => string.Equals(other.ServerId, serverId, StringComparison.Ordinal))
            ? serverId
            : null;

    // AC-403: whether `entry` is the token held for `serverId` — matched by id if it has one, else by the legacy
    // id its name derives to. Deliberately never falls back to the server's *current* name, since two servers
    // swapping names would otherwise adopt each other's token.
    internal static bool Matches(McpOAuthTokenEntry entry, string serverId) =>
        !string.IsNullOrEmpty(entry.ServerId)
            ? string.Equals(entry.ServerId, serverId, StringComparison.Ordinal)
            : string.Equals(McpServerIdentity.LegacyIdFor(entry.ServerName), serverId, StringComparison.Ordinal);
}
