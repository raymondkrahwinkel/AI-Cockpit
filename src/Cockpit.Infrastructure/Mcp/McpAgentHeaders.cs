using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Mcp;

/// <summary>
/// The operator's own headers (AC-354) as a spawned agent's config should carry them. One rule in one place, for the
/// same reason <see cref="CockpitMcpBearer"/> is: it is applied on both spawn paths, and a precedence rule copied
/// twice is a precedence rule that drifts.
/// </summary>
internal static class McpAgentHeaders
{
    /// <summary>
    /// The headers to send, minus anything the credential already covers. When a bearer is going out,
    /// <c>Authorization</c> is dropped here rather than left for each provider to resolve: the three config writers
    /// each build their headers differently, so leaving both in would make which one wins a property of whichever
    /// provider is running. Half-written rows are dropped — a blank field name is not a header.
    /// </summary>
    public static IReadOnlyDictionary<string, string> For(McpServerConfig server, string? bearerToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in server.Headers.Where(header => header.IsComplete))
        {
            headers[header.Name] = header.Value;
        }

        // A cockpit-hosted endpoint carries no literal bearer here — its auth rides an env var the provider
        // references — but it is still an Authorization the operator must not be able to displace by hand.
        if (server.CockpitHosted || !string.IsNullOrWhiteSpace(bearerToken))
        {
            headers.Remove("Authorization");
        }

        return headers;
    }
}
