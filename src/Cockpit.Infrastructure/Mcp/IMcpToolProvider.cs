namespace Cockpit.Infrastructure.Mcp;

/// <summary>
/// Connects to the enabled MCP servers in the shared registry (#26) and exposes their tools for a local
/// session's agentic tool-loop. A server that fails to start or is unreachable is skipped so the session
/// still runs with whatever connected.
/// </summary>
internal interface IMcpToolProvider
{
    Task<IMcpToolSession> ConnectAsync(CancellationToken cancellationToken = default);
}
