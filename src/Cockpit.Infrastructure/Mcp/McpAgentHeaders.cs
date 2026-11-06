using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Mcp;

// Upstream headers composed by Cockpit; these never enter the spawned agent's MCP config.
internal static class McpAgentHeaders
{
    // Headers to send, minus the one the cockpit answers for and any half-written row. Authorization is dropped
    // for every McpServerAuth except None, since in-process OAuth negotiates its own header and a second one
    // would collide with it.
    public static IReadOnlyDictionary<string, string> For(McpServerConfig server, string? bearerToken, string? agentName = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in server.Headers.Where(header => header.IsComplete))
        {
            headers[header.Name] = header.Value;
        }

        if (server.CockpitHosted || server.Auth != McpServerAuth.None || !string.IsNullOrWhiteSpace(bearerToken))
        {
            headers.Remove("Authorization");
        }

        if (server.IncludeAgentName && !string.IsNullOrWhiteSpace(agentName))
        {
            headers["X-Cockpit-Agent"] = agentName;
        }

        return headers;
    }
}
