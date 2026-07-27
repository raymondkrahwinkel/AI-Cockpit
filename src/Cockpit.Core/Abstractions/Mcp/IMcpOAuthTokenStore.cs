using Cockpit.Core.Mcp;

namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// Persists the OAuth tokens the cockpit obtained for MCP servers (AC-353), in the <c>mcpOAuthTokens</c> section of
/// <c>cockpit.json</c>. One place, so that a single sign-in serves every session route and a token that has to be
/// withdrawn is withdrawn once rather than hunted across four agents' own credential stores.
/// </summary>
public interface IMcpOAuthTokenStore
{
    /// <summary>The token held for <paramref name="serverName"/>, or <see langword="null"/> if nobody has signed in.</summary>
    Task<McpOAuthToken?> GetAsync(string serverName, CancellationToken cancellationToken = default);

    /// <summary>Records the token for <paramref name="serverName"/>, replacing any earlier one.</summary>
    Task SaveAsync(string serverName, McpOAuthToken token, CancellationToken cancellationToken = default);

    /// <summary>Forgets the token for <paramref name="serverName"/>. Removing one that is not there is not an error.</summary>
    Task RemoveAsync(string serverName, CancellationToken cancellationToken = default);
}
