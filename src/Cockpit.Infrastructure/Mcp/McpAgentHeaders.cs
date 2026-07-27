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
    /// The headers to send, minus the one the cockpit is already answering for. Half-written rows are dropped — a
    /// blank field name is not a header.
    /// <para>
    /// <c>Authorization</c> is dropped whenever this server's authentication is the cockpit's to arrange, which is
    /// every case except <see cref="McpServerAuth.None"/>. That is wider than "a bearer is going out", and the
    /// difference is the OAuth server: in-process no bearer is produced at all, because the MCP SDK negotiates the
    /// authorization itself and a second Authorization would collide with it — so testing for a token would have
    /// left the hand-typed one in place on exactly the route that cannot take it. A server set to
    /// <see cref="McpServerAuth.None"/> may still name <c>Authorization</c> by hand: a scheme other than Bearer on
    /// the standard header is one of the things this feature exists for, and there nothing is competing with it.
    /// </para>
    /// </summary>
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
