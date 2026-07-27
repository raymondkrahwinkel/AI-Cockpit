using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Mcp;

/// <summary>
/// Decides the bearer token a session (or the in-app tool loop) presents to one MCP server (AC-40). One rule in one
/// place, because it is applied on three paths — the Claude/Codex spawn adapters and the local-model tool provider —
/// and a security check that is copied three times is a security check that drifts.
/// </summary>
internal static class CockpitMcpBearer
{
    /// <summary>
    /// The token for an <em>in-process</em> client (the local-model tool loop): the app-lifetime key for a
    /// cockpit-hosted loopback endpoint, the server's own static API key for a user API-key server, or none. The app
    /// key is handed only to an endpoint the cockpit runs — never to a user-added server, which would be leaking the
    /// host's key to a third party.
    /// </summary>
    public static string? For(McpServerConfig server, McpAuthKey authKey) =>
        server.CockpitHosted ? authKey.Value : UserApiKey(server);

    /// <summary>
    /// A user API-key server's own static token, or none. This is all a spawned CLI's config carries as a literal:
    /// a cockpit-hosted endpoint's auth rides the <c>COCKPIT_MCP_KEY</c> env var instead (never a literal on disk),
    /// so this deliberately returns null for it.
    /// </summary>
    public static string? UserApiKey(McpServerConfig server) =>
        server.Auth == McpServerAuth.ApiKey && !string.IsNullOrWhiteSpace(server.ApiKey)
            ? server.ApiKey
            : null;

    /// <summary>
    /// The literal credential a <em>spawned</em> agent's MCP config carries for this server: the static API key as
    /// before, or — new in AC-353 — the access token the cockpit obtained by signing in on the operator's behalf,
    /// passed in by the caller that resolved it.
    /// <para>
    /// This is where the two worlds part. In-process (<see cref="For"/>) an OAuth server needs no header at all,
    /// because the MCP SDK negotiates the authorization itself and would collide with one; a spawned CLI has no such
    /// machinery pointed at the cockpit's token, so for it the token has to be spelled out. Everything else — a
    /// cockpit-hosted endpoint's key never being written as a literal — is unchanged.
    /// </para>
    /// </summary>
    public static string? UserCredential(McpServerConfig server, string? oauthAccessToken) =>
        UserApiKey(server)
        ?? (server.Auth == McpServerAuth.OAuth && !string.IsNullOrWhiteSpace(oauthAccessToken) ? oauthAccessToken : null);
}
