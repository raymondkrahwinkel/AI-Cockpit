using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Mcp;

// AC-134: IMcpToolTokenEstimator over the shared MCP tool provider — connects one server, serialises its tools,
// counts characters at McpToolTokenMath's ratio, and caches the estimate since connecting is expensive. A server
// that can't connect caches as McpServerToolEstimate.Unavailable rather than a false zero.
internal sealed class McpToolTokenEstimator(IMcpToolProvider toolProvider, ILogger<McpToolTokenEstimator> logger)
    : IMcpToolTokenEstimator, ISingletonService
{
    // AC-134: Lazy<Task> per server, not a completed value, so concurrent estimates single-flight instead of
    // several MCP-restricting profiles each spawning their own before the first result lands.
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
            var tools = await toolProvider.EnumerateServerToolsAsync(serverName, cancellationToken: CancellationToken.None).ConfigureAwait(false);

            // Null = the server could not be enumerated (unknown, disabled, OAuth-gated, or unreachable): unknown
            // cost, not zero.
            if (tools is null)
            {
                return McpServerToolEstimate.Unavailable(serverName);
            }

            var tokens = McpToolTokenMath.EstimateTokens(tools.Select(SerialiseForEstimate));
            return new McpServerToolEstimate(serverName, tools.Count, tokens, Available: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not estimate MCP tool tokens for {Server}", serverName);
            return McpServerToolEstimate.Unavailable(serverName);
        }
    }

    // A tool as the model sees it in the prompt: its name, its description, and its JSON input schema.
    // Also what the local-model driver weighs when it reports how much schema its search mode keeps out of a
    // request (AC-963) — one formula, so the two numbers stay comparable.
    internal static string SerialiseForEstimate(AIFunction tool) =>
        $"{tool.Name}\n{tool.Description}\n{tool.JsonSchema.GetRawText()}";
}
