using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Mcp;

// The operator's own headers (AC-354) as a spawned agent's config should carry them. One rule in one place, for the
// same reason `CockpitMcpBearer` is: it is applied on both spawn paths, and a precedence rule copied
// twice is a precedence rule that drifts.
internal static class McpAgentHeaders
{
    // Headers to send, minus the one the cockpit answers for and any half-written row. Authorization is dropped
    // for every McpServerAuth except None, since in-process OAuth negotiates its own header and a second one
    // would collide with it.
    public static IReadOnlyDictionary<string, string> For(McpServerConfig server, string? bearerToken)
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

        return headers;
    }
}
