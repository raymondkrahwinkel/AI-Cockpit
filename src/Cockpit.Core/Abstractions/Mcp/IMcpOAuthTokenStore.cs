using Cockpit.Core.Mcp;

namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// Persists the OAuth tokens the cockpit obtained for MCP servers (AC-353), in <c>cockpit.json</c>'s
/// <c>mcpOAuthTokens</c> section — one place, so a sign-in serves every route and a withdrawn token is withdrawn
/// once, not hunted across stores. Keyed by <see cref="McpServerConfig.IdentityKey"/>, not the name (AC-403): a renamed server otherwise leaves a token behind, unreachable, and two servers swapping names would swap tokens.
/// </summary>
public interface IMcpOAuthTokenStore
{
    /// <summary>The token held for <paramref name="serverId"/>, or <see langword="null"/> if nobody has signed in.</summary>
    Task<McpOAuthToken?> GetAsync(string serverId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the token for <paramref name="serverId"/>, replacing any earlier one. <paramref name="serverName"/>
    /// is written alongside it as a label so the file stays readable — it is never matched on.
    /// </summary>
    Task SaveAsync(string serverId, string serverName, McpOAuthToken token, CancellationToken cancellationToken = default);

    /// <summary>Forgets the token for <paramref name="serverId"/>. Removing one that is not there is not an error.</summary>
    Task RemoveAsync(string serverId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-keys tokens an older build filed under a server's name onto its current id (AC-403), for servers whose id
    /// can't be derived from the name. Already-covered entries are left alone. ⚠️ Run only before the operator can rename anything — safe exactly once at startup, or renamed servers would swap tokens.
    /// </summary>
    /// <param name="idsByServerName">Every known server's current name mapped to the id it should be filed under.</param>
    Task AdoptLegacyEntriesAsync(IReadOnlyDictionary<string, string> idsByServerName, CancellationToken cancellationToken = default);
}
