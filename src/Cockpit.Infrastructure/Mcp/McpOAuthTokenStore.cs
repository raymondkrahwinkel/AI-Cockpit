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
        // Read first and leave without writing at all when nothing would move — which is every launch after the one
        // that migrates, and every launch of an install that never had a plugin-keyed token to begin with. This runs
        // on the startup path, and rewriting cockpit.json each time to change nothing is churn on a file the
        // operator hand-edits and every other section store shares.
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (configFile?.McpOAuthTokens is null
            || !configFile.McpOAuthTokens.Any(entry => _AdoptableId(configFile.McpOAuthTokens, entry, idsByServerName) is not null))
        {
            return;
        }

        await _configFile.UpdateAsync(
            file =>
            {
                // Re-decided against the list this update actually holds rather than against the snapshot above: the
                // read happened outside the write gate, so what was concluded there is a hint, not a fact.
                // ToList() because the loop assigns into the entries it is walking, and that is exactly what the
                // "does anyone already hold this id" check reads back.
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

    /// <summary>
    /// The id <paramref name="entry"/> should be re-keyed onto, or <see langword="null"/> when it must be left
    /// exactly as it is. Four ways to be left alone: it already carries an id — a real sign-in, which a guess made
    /// from a name may never overwrite; no server currently answers to its name; the id offered is the one its own
    /// name already derives to, so it is reachable without writing anything; or another entry already holds that id,
    /// which is what stops two of these collapsing onto a single credential.
    /// </summary>
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
