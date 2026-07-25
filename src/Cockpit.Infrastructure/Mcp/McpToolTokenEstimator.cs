using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Mcp;

/// <summary>
/// <see cref="IMcpToolTokenEstimator"/> over the shared MCP tool provider (AC-134): to estimate a server's tool
/// tokens it connects that one server, reads the tools it exposes, serialises each (name + description + JSON
/// schema), and counts characters at <see cref="McpToolTokenMath"/>'s ratio. Connecting is the expensive part
/// (a stdio server spawns a process), so each server's estimate is cached and only recomputed on an explicit
/// refresh. A server that cannot be connected — unreachable, or it needs an auth this pre-flight does not do —
/// caches as <see cref="McpServerToolEstimate.Unavailable"/> so the UI shows "unknown" rather than a false zero.
/// </summary>
internal sealed class McpToolTokenEstimator(IMcpToolProvider toolProvider, ILogger<McpToolTokenEstimator> logger)
    : IMcpToolTokenEstimator, ISingletonService
{
    // A Lazy<Task> per server, not a completed value, so concurrent estimates for the same server share one
    // enumeration (single-flight): several MCP-restricting profiles all counting on the Manage-profiles load would
    // otherwise each spawn it before the first result landed (AC-134 review). The result stays cached; a refresh
    // replaces the entry with a fresh run.
    private readonly ConcurrentDictionary<string, Lazy<Task<McpServerToolEstimate>>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public Task<McpServerToolEstimate> EstimateAsync(string serverName, bool refresh = false, CancellationToken cancellationToken = default)
    {
        var run = new Lazy<Task<McpServerToolEstimate>>(() => _EnumerateAsync(serverName));
        if (refresh)
        {
            _cache[serverName] = run;
            return run.Value;
        }

        return _cache.GetOrAdd(serverName, run).Value;
    }

    // The enumeration is deliberately not bound to any one caller's cancellation token: the estimate is shared,
    // background, best-effort work, so one dialog closing must not cancel an enumeration another is still awaiting —
    // and the connect scope tears its process down on its own regardless.
    private async Task<McpServerToolEstimate> _EnumerateAsync(string serverName)
    {
        try
        {
            var tools = await toolProvider.EnumerateServerToolsAsync(serverName, CancellationToken.None).ConfigureAwait(false);

            // Null = the server could not be enumerated (unknown, disabled, OAuth-gated, or unreachable): unknown
            // cost, not zero.
            if (tools is null)
            {
                return McpServerToolEstimate.Unavailable(serverName);
            }

            var tokens = McpToolTokenMath.EstimateTokens(tools.Select(_Serialise));
            return new McpServerToolEstimate(serverName, tools.Count, tokens, Available: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not estimate MCP tool tokens for {Server}", serverName);
            return McpServerToolEstimate.Unavailable(serverName);
        }
    }

    /// <summary>A tool as the model sees it in the prompt: its name, its description, and its JSON input schema.</summary>
    private static string _Serialise(AIFunction tool) =>
        $"{tool.Name}\n{tool.Description}\n{tool.JsonSchema.GetRawText()}";
}
