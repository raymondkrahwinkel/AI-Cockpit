using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Mcp;

// Decides the bearer token a session (or the in-app tool loop) presents to one MCP server (AC-40). One rule in one
// place, because it is applied on three paths — the Claude/Codex spawn adapters and the local-model tool provider —
// and a security check that is copied three times is a security check that drifts.
internal static class CockpitMcpBearer
{
    // In-process client token: the app-lifetime key for a cockpit-hosted endpoint, a user server's static API key,
    // or none — the app key is never handed to a user-added server.
    public static string? For(McpServerConfig server, McpAuthKey authKey) =>
        server.CockpitHosted ? authKey.Value : UserApiKey(server);

    // A user API-key server's own static token, or none. This is all a spawned CLI's config carries as a literal:
    // a cockpit-hosted endpoint's auth rides the `COCKPIT_MCP_KEY` env var instead (never a literal on disk),
    // so this deliberately returns null for it.
    public static string? UserApiKey(McpServerConfig server) =>
        server.Auth == McpServerAuth.ApiKey && !string.IsNullOrWhiteSpace(server.ApiKey)
            ? server.ApiKey
            : null;

    // AC-353: the literal credential a spawned agent's MCP config carries — a static API key, or an OAuth access
    // token the cockpit obtained on the operator's behalf; in-process OAuth needs no header since the SDK
    // negotiates it itself.
    public static string? UserCredential(McpServerConfig server, string? oauthAccessToken) =>
        UserApiKey(server)
        ?? (server.Auth == McpServerAuth.OAuth && !string.IsNullOrWhiteSpace(oauthAccessToken) ? oauthAccessToken : null);
}
