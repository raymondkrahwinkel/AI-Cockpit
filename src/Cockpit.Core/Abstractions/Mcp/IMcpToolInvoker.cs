namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// Calls one tool on one configured MCP server on the app's own behalf (AC-502) — not inside a session's own
/// tool-loop, but for a plugin that needs an answer from its own MCP server before any session exists (a project
/// editor's picker asking a Depot connection to list its projects, say). Reuses the same connect path, OAuth
/// standing and token as a session would for that server, so the result is exactly what a session's own call to
/// the same tool would see.
/// </summary>
public interface IMcpToolInvoker
{
    /// <summary>
    /// Calls <paramref name="toolName"/> on the enabled registry server named <paramref name="serverName"/>. Never
    /// throws for an ordinary failure — a missing/disabled server, an OAuth server that still needs an interactive
    /// sign-in, an unreachable endpoint, or the tool call itself failing all come back as a named
    /// <see cref="McpToolInvocationResult"/> outcome rather than an exception, since this is meant to be called from
    /// a UI path that reports the outcome rather than crashing on it.
    /// </summary>
    Task<McpToolInvocationResult> InvokeAsync(
        string serverName,
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments = null,
        string? projectId = null,
        CancellationToken cancellationToken = default);
}
