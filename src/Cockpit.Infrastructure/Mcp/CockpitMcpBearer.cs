using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Mcp;

// Credentials for Cockpit's upstream clients; user servers must never receive the app key.
internal static class CockpitMcpBearer
{
    // In-process client token: the app-lifetime key for a cockpit-hosted endpoint, a user server's static API key,
    // or none — the app key is never handed to a user-added server.
    public static string? For(McpServerConfig server, McpAuthKey authKey) =>
        server.CockpitHosted ? authKey.Value : UserApiKey(server);

    // A user API-key server's own static token, or none.
    public static string? UserApiKey(McpServerConfig server) =>
        server.Auth == McpServerAuth.ApiKey && !string.IsNullOrWhiteSpace(server.ApiKey)
            ? server.ApiKey
            : null;

}
