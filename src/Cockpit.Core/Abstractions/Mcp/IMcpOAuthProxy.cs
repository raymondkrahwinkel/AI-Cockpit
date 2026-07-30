using Cockpit.Core.Mcp;

namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// Stands a loopback address in front of an OAuth-protected MCP server so a spawned agent never holds the OAuth
/// token itself (AC-524).
/// <para>
/// The reason it exists is measured, not assumed: the Claude CLI reads its <c>--mcp-config</c> exactly once, when
/// it opens the connection, and never again — not after a 401, not across a reconnect, not after the file has been
/// rewritten or deleted. So whatever token that file carried is the token that session has for its whole life, and
/// any lifetime is eventually too short. Pointing the config at an address the cockpit owns moves the credential
/// out of the file and onto each individual request, where it can be renewed.
/// </para>
/// </summary>
public interface IMcpOAuthProxy
{
    /// <summary>
    /// The loopback URL to write into a session's config in place of <paramref name="server"/>'s own address, or
    /// <see langword="null"/> when there is none to give — the server is not OAuth-protected, has no HTTP address,
    /// or the listener could not be bound. A caller that gets <see langword="null"/> falls back to writing the token
    /// itself: degraded, but no worse than before this existed.
    /// <para>
    /// Idempotent per server and address: the endpoint outlives the session that first asked for it, so a second
    /// session gets the one already listening rather than a second listener for the same server.
    /// </para>
    /// </summary>
    Task<string?> MountAsync(McpServerConfig server, CancellationToken cancellationToken = default);
}
