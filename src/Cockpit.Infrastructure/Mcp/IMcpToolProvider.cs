using Microsoft.Extensions.AI;

namespace Cockpit.Infrastructure.Mcp;

/// <summary>
/// Connects to the enabled MCP servers in the shared registry (#26) and exposes their tools for a local
/// session's agentic tool-loop. A server that fails to start or is unreachable is skipped so the session
/// still runs with whatever connected.
/// </summary>
internal interface IMcpToolProvider
{
    /// <summary>
    /// <paramref name="enabledServerNames"/> is the per-session MCP selection from the New-session dialog
    /// (#44): when non-null, only registry servers named in it are connected, on top of the registry's own
    /// enabled/scope filtering. <see langword="null"/> keeps the pre-#44 behaviour of using every eligible
    /// registry server.
    /// </summary>
    Task<IMcpToolSession> ConnectAsync(IReadOnlySet<string>? enabledServerNames = null, string? paneId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Connects a single named catalog server on its own, just to read its tool list for the pre-flight token
    /// estimate (AC-134). Unlike <see cref="ConnectAsync"/> it does NOT merge the built-in local-default servers
    /// (filesystem/fetch/git/…) — a count must estimate only the server the operator ticked, not spawn processes
    /// they never chose — and it skips an OAuth server rather than driving its interactive browser sign-in. Returns
    /// null when the server is unknown, disabled, OAuth-gated, or could not be enumerated, so the caller shows it as
    /// "unknown" rather than a false zero.
    /// </summary>
    Task<IReadOnlyList<AIFunction>?> EnumerateServerToolsAsync(string serverName, CancellationToken cancellationToken = default);
}
