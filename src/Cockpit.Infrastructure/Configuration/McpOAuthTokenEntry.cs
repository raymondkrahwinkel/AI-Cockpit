using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of an `McpOAuthToken` in the `mcpOAuthTokens` section of `cockpit.json`.
//
// The field names are load-bearing: `SecretFields` decides what to encrypt and what to empty out of a backup by
// the name of the field, so `AccessToken` and `RefreshToken` are covered by the existing
// `token` rule without any plumbing of their own. The scheme is called `Scheme` rather than
// `TokenType` for the same reason, the other way round — it holds the word "Bearer", and a name matching the
// rule would have it needlessly encrypted.
internal sealed class McpOAuthTokenEntry
{
    // The `McpServerConfig.IdentityKey` this token belongs to (AC-403) — the one field the store
    // matches on. Empty only for an entry an older build wrote, which is what `ServerName` is still
    // read for.
    public string ServerId { get; set; } = string.Empty;

    // The `McpServerConfig.Name` this token was last written for — a label, so the section stays
    // readable to whoever opens `cockpit.json`.
    //
    // ⚠️ *Never match a server against this.* It is a copy of a name the operator can change, and it goes
    // stale the moment they do; that staleness is AC-403 itself. The single exception is an entry with no
    // `ServerId` — one an older build wrote, whose name *was* its key — and even there the
    // store compares against the id that name derives to, not against any server's current name.
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
