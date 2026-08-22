using Cockpit.Core.Mcp;

namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// Stands a loopback address in front of an OAuth-protected MCP server so a spawned agent never holds the token
/// itself (AC-524). Exists because the Claude CLI reads its <c>--mcp-config</c> exactly once, at connect — never
/// after a 401, on reconnect, or after the file changes — so whatever token it carried is the token that session has for life. Moves the credential out of the file and onto each request, where it can be renewed.
/// </summary>
public interface IMcpOAuthProxy
{
    /// <summary>
    /// The loopback URL for a session's config in place of <paramref name="server"/>'s own, or
    /// <see langword="null"/> when there's none to give (not OAuth-protected, no HTTP address, or bind failed) — a
    /// caller getting null falls back to writing the token itself, degraded but no worse than before. Idempotent: the endpoint outlives the session that asked, so a later one gets the one already listening.
    /// </summary>
    Task<string?> MountAsync(McpServerConfig server, CancellationToken cancellationToken = default);
}
