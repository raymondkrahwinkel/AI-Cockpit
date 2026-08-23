using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of an `McpOAuthToken` in the `mcpOAuthTokens` section. Field names are load-bearing:
// `SecretFields` matches by name, so `AccessToken`/`RefreshToken` are covered by the `token` rule
// automatically, and `Scheme` (not `TokenType`) avoids that same rule needlessly encrypting "Bearer".
internal sealed class McpOAuthTokenEntry
{
    // The `McpServerConfig.IdentityKey` this token belongs to (AC-403) — the one field the store
    // matches on. Empty only for an entry an older build wrote, which is what `ServerName` is still
    // read for.
    public string ServerId { get; set; } = string.Empty;

    // AC-403: the server name this token was last written for — a display label only, never match a
    // server against it, since it goes stale the moment the operator renames the server. The one
    // exception, a legacy entry with no `ServerId`, still compares against the id the name derives to.
    public string ServerName { get; set; } = string.Empty;

    public string AccessToken { get; set; } = string.Empty;

    public string Scheme { get; set; } = "Bearer";

    public string? RefreshToken { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public string? Scope { get; set; }

    // The endpoint the token was obtained for, so a server that later answers to the same name under a different address does not inherit it.
    public string? ResourceUrl { get; set; }

    // The OAuth client id these tokens were issued to (AC-505). Without this surviving a restart, a stored
    // `RefreshToken` is dead on arrival: the SDK only attempts a refresh grant once it has a client
    // identity to present, and a fresh process has none until this is read back.
    public string? ClientId { get; set; }

    // The client secret paired with `ClientId` — covered by the existing `secret` rule in `SecretFields`, same as `AccessToken`/`RefreshToken` are by `token`.
    public string? ClientSecret { get; set; }

    public string? TokenEndpointAuthMethod { get; set; }

    // The authorization server `ClientId` was registered with.
    public string? AuthorizationServer { get; set; }

    public static McpOAuthTokenEntry FromDomain(string serverId, string serverName, McpOAuthToken token) => new()
    {
        ServerId = serverId,
        ServerName = serverName,
        AccessToken = token.AccessToken,
        Scheme = token.Scheme,
        RefreshToken = token.RefreshToken,
        ExpiresAt = token.ExpiresAt,
        Scope = token.Scope,
        ResourceUrl = token.ResourceUrl,
        ClientId = token.ClientId,
        ClientSecret = token.ClientSecret,
        TokenEndpointAuthMethod = token.TokenEndpointAuthMethod,
        AuthorizationServer = token.AuthorizationServer,
    };

    public McpOAuthToken ToDomain() => new()
    {
        AccessToken = AccessToken,
        Scheme = Scheme,
        RefreshToken = RefreshToken,
        ExpiresAt = ExpiresAt,
        Scope = Scope,
        ResourceUrl = ResourceUrl,
        ClientId = ClientId,
        ClientSecret = ClientSecret,
        TokenEndpointAuthMethod = TokenEndpointAuthMethod,
        AuthorizationServer = AuthorizationServer,
    };
}
