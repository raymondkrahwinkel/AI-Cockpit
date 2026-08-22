using Cockpit.Core.Mcp;

namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// Calls one tool on one configured MCP server on the app's own behalf (AC-502) — for a plugin needing an answer
/// from its own MCP server before any session exists (a project editor's picker asking Depot to list its projects).
/// Reuses the connect path, OAuth standing and token a session would use, so results match a session's own call.
/// </summary>
public interface IMcpToolInvoker
{
    /// <summary>
    /// Calls <paramref name="toolName"/> on server <paramref name="serverName"/>, resolved against the catalog for
    /// <paramref name="projectId"/> and, only if nothing is found, against <paramref name="callerFallbackServers"/>
    /// (AC-499) — a plugin calling its own server before its project row is saved. Never throws: failures come back as a named <see cref="McpToolInvocationResult"/>, since this is a UI path that reports outcomes.
    /// </summary>
    Task<McpToolInvocationResult> InvokeAsync(
        string serverName,
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments = null,
        string? projectId = null,
        IReadOnlyList<McpServerConfig>? callerFallbackServers = null,
        CancellationToken cancellationToken = default);
}
