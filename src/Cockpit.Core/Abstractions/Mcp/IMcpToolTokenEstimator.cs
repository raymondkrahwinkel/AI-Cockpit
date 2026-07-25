namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// A pre-flight estimate of the prompt tokens a single MCP server's tool definitions add to a session (AC-134):
/// how many tools it exposes and roughly how many tokens their names, descriptions and JSON schemas take up.
/// <paramref name="Available"/> is false when the server could not be enumerated (unreachable, or it needs an
/// auth the estimate step does not perform), so the UI can show "unknown" rather than a misleading zero.
/// </summary>
public sealed record McpServerToolEstimate(string ServerName, int ToolCount, int EstimatedTokens, bool Available)
{
    /// <summary>An unknown estimate for a server that could not be enumerated.</summary>
    public static McpServerToolEstimate Unavailable(string serverName) => new(serverName, 0, 0, Available: false);
}

/// <summary>
/// Estimates, per MCP server, the prompt tokens its tool definitions cost — so the New-session dialog and the
/// profile editor can show a running total for the ticked servers before a session starts, instead of the
/// operator only finding out at an <c>exceed_context_size_error</c> (AC-134). Deriving the number means
/// connecting the server and reading its <c>tools/list</c> (a config alone has no tool schemas), which is the
/// cost — so results are cached per server and only recomputed on an explicit refresh.
/// </summary>
public interface IMcpToolTokenEstimator
{
    /// <summary>
    /// The tool-token estimate for <paramref name="serverName"/>, from cache when present. <paramref name="refresh"/>
    /// re-enumerates the server and replaces the cached value. A server that cannot be connected comes back as
    /// <see cref="McpServerToolEstimate.Unavailable"/>.
    /// </summary>
    Task<McpServerToolEstimate> EstimateAsync(string serverName, bool refresh = false, CancellationToken cancellationToken = default);
}
