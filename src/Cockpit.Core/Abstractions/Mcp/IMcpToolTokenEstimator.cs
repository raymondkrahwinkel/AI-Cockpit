namespace Cockpit.Core.Abstractions.Mcp;

// A pre-flight estimate of the prompt tokens a single MCP server's tool definitions add to a session (AC-134):
// how many tools it exposes and roughly how many tokens their names, descriptions and JSON schemas take up.
// `Available` is false when the server could not be enumerated (unreachable, or it needs an
// auth the estimate step does not perform), so the UI can show "unknown" rather than a misleading zero.
public sealed record McpServerToolEstimate(string ServerName, int ToolCount, int EstimatedTokens, bool Available)
{
    // An unknown estimate for a server that could not be enumerated.
    public static McpServerToolEstimate Unavailable(string serverName) => new(serverName, 0, 0, Available: false);
}

/// <summary>
/// Estimates, per MCP server, the prompt tokens its tools cost — so the New-session dialog and profile editor show
/// a running total before start, instead of the operator hitting <c>exceed_context_size_error</c> (AC-134). Deriving
/// it means connecting and reading <c>tools/list</c>, the cost — so results are cached and recomputed only on refresh.
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
