namespace Cockpit.Core.Mcp;

// AC-353: the credential the cockpit holds for one OAuth-protected MCP server, kept apart from
// `McpServerConfig` because that record is rewritten in full on every edit and would drop a token riding
// along with it — the operator's config and the sign-in's yield have different lifetimes.
public sealed record McpOAuthToken
{
    // The access token presented as `Authorization: &lt;scheme&gt; &lt;token&gt;`.
    public required string AccessToken { get; init; }

    // The authentication scheme the server named, in practice always `Bearer`.
    public string Scheme { get; init; } = "Bearer";

    // The refresh token, when the authorization server issued one — what lets a stale access token be renewed without asking the operator again.
    public string? RefreshToken { get; init; }

    // When the access token stops being accepted, or `null` if the server named no lifetime.
    public DateTimeOffset? ExpiresAt { get; init; }

    // The scope the token was granted, as returned by the authorization server.
    public string? Scope { get; init; }

    // The endpoint this token was obtained for. What binds the credential to a host rather than to a name.
    public string? ResourceUrl { get; init; }

    // AC-505: OAuth client ID these tokens were issued to. Without it a refresh token is unusable beyond the
    // connection that obtained it, since the SDK builds a fresh `ClientOAuthProvider` per connect attempt with
    // no client identity until `RestoreCachedClientCredentials` reads this back.
    public string? ClientId { get; init; }

    // The OAuth client secret paired with `ClientId`, when dynamic client registration issued one.
    // A credential in its own right (Iron Law #8) — masked in `ToString` exactly like
    // `AccessToken` and `RefreshToken`.
    public string? ClientSecret { get; init; }

    // The token endpoint authentication method negotiated for `ClientId` (e.g. `client_secret_post`).
    public string? TokenEndpointAuthMethod { get; init; }

    // The authorization server issuer `ClientId` was registered with. The SDK will not restore a
    // cached client identity for a different authorization server than the one it is currently talking to — the
    // same origin discipline `IsForResource` applies to the resource itself.
    public string? AuthorizationServer { get; init; }

    // Whether this token may be presented to `url`. A stored token is found by name, not identity — a project or
    // rename can point the same name at a different address — so the issued origin must match the target origin
    // (scheme/host/port; a token with no recorded origin is never used) or the credential could leak to an impostor.
    public bool IsForResource(string? url) =>
        Uri.TryCreate(ResourceUrl, UriKind.Absolute, out var issuedFor)
        && Uri.TryCreate(url, UriKind.Absolute, out var target)
        && string.Equals(issuedFor.Scheme, target.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(issuedFor.Host, target.Host, StringComparison.OrdinalIgnoreCase)
        && issuedFor.Port == target.Port;

    // Overrides the record's generated `ToString()`, which would otherwise print both tokens in full anywhere
    // this lands in a log line or an exception message (Iron Law #8) — the same guard `PluginMcpServer` carries
    // for the credential it holds.
    public override string ToString() =>
        $"{nameof(McpOAuthToken)} {{ {nameof(Scheme)} = {Scheme}, {nameof(AccessToken)} = ***, "
        + $"{nameof(RefreshToken)} = {(string.IsNullOrEmpty(RefreshToken) ? "null" : "***")}, "
        + $"{nameof(ExpiresAt)} = {ExpiresAt}, {nameof(Scope)} = {Scope}, {nameof(ResourceUrl)} = {ResourceUrl}, "
        + $"{nameof(ClientId)} = {ClientId}, {nameof(ClientSecret)} = {(string.IsNullOrEmpty(ClientSecret) ? "null" : "***")}, "
        + $"{nameof(TokenEndpointAuthMethod)} = {TokenEndpointAuthMethod}, {nameof(AuthorizationServer)} = {AuthorizationServer} }}";

    // Whether this token can still be handed to an agent at `moment`, keeping `margin` in hand — otherwise a token
    // expiring in two seconds could be written into a config a session reads for the next hour. No named expiry
    // is taken at face value; guessing would either discard a working credential or claim a dead one.
    public bool IsUsableAt(DateTimeOffset moment, TimeSpan margin) =>
        !string.IsNullOrWhiteSpace(AccessToken) && (ExpiresAt is null || ExpiresAt.Value - margin > moment);
}
