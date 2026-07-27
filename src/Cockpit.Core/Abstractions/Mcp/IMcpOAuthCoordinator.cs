using Cockpit.Core.Mcp;

namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// Owns the cockpit's standing with the OAuth-protected MCP servers (AC-353): it answers what credential a session
/// may present, and renews one that has gone stale.
/// <para>
/// The distinction that matters is <em>interactive</em>. Starting a session must never open a browser the operator
/// did not ask for, so it asks non-interactively and is told <see cref="McpAuthState.AuthorizationRequired"/> when a
/// renewal is not possible without a person. Signing in is the operator's own act, and only that asks interactively.
/// </para>
/// </summary>
public interface IMcpOAuthCoordinator
{
    /// <summary>
    /// The credential to present to <paramref name="server"/>. A server that is not OAuth-protected answers
    /// <see cref="McpOAuthAccess.NotRequired"/> without any work. When <paramref name="interactive"/> is
    /// <see langword="false"/> this never opens a browser: a token that cannot be renewed silently comes back as
    /// <see cref="McpAuthState.AuthorizationRequired"/> instead.
    /// </summary>
    Task<McpOAuthAccess> AcquireAsync(McpServerConfig server, bool interactive, CancellationToken cancellationToken = default);
}
