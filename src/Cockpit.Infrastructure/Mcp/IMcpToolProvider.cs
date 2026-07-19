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
}
