using Cockpit.Core.Mcp;

namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// Persists the OAuth tokens the cockpit obtained for MCP servers (AC-353), in the <c>mcpOAuthTokens</c> section of
/// <c>cockpit.json</c>. One place, so that a single sign-in serves every session route and a token that has to be
/// withdrawn is withdrawn once rather than hunted across four agents' own credential stores.
/// <para>
/// Keyed by <see cref="McpServerConfig.IdentityKey"/> rather than the server's name (AC-403). A name is something
/// the operator edits; a token filed under one is left behind by the rename that follows, unreachable and still
/// carrying a refresh token — and, for two servers on the same host that swap names, offered to the endpoint it
/// was never issued for.
/// </para>
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
    /// Re-keys tokens an older build filed under a server's name onto the id that server carries now (AC-403), for
    /// the servers whose id cannot be derived back from their name — a plugin that mints its own, where the name is
    /// something else entirely. A server the derivation already covers needs nothing, and a token already carrying
    /// an id is left alone.
    /// <para>
    /// ⚠️ Run this <em>before</em> the operator has any way to rename something, and only then. It is the one place
    /// that matches a token against a server's <em>current</em> name, which is safe exactly once: at startup, on a
    /// file whose names still say what they said when the tokens were written. Run it later and two servers that
    /// swapped names in the meantime would swap tokens — the defect this whole ticket exists to remove.
    /// </para>
    /// </summary>
    /// <param name="idsByServerName">Every known server's current name mapped to the id it should be filed under.</param>
    Task AdoptLegacyEntriesAsync(IReadOnlyDictionary<string, string> idsByServerName, CancellationToken cancellationToken = default);
}
