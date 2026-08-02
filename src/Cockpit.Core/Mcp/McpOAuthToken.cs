namespace Cockpit.Core.Mcp;

// The credential the cockpit holds for one OAuth-protected MCP server (AC-353), kept so that a single browser
// sign-in serves every session route and survives a restart of the app.
//
// This is deliberately not part of `McpServerConfig`: that record is the operator's own configuration,
// rewritten in full every time the server is edited, and a token riding along there would be dropped by the first
// save. What the operator types and what the sign-in yielded have different lifetimes, so they are stored apart.
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

    // The OAuth client ID these tokens were issued to (AC-505). Without this, a refresh token is unusable beyond
    // the connection that obtained it: the SDK builds a fresh `ClientOAuthProvider` per connect attempt (a new
    // session, a renewal, a restart), which starts with no client identity of its own, and it will only try a
    // refresh grant once it has one to present. Persisting it here is what `ClientOAuthProvider`'s own
    // `RestoreCachedClientCredentials` is built to read back — the mechanism the SDK's own doc comments
    // describe as letting "a persisted refresh token be used after a restart without re-running dynamic client
    // registration".
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

    // Whether this token may be presented to `url`. A stored token is found by the server's name,
    // and a name is not an identity: a project can replace a registry server with its own entry under the same name
    // and a different address, and the operator can rename or duplicate one. Without this check the credential
    // obtained for one host would be handed to whatever now answers to that name — so the origin it was issued for
    // has to match the origin it is about to be sent to, and a token with no recorded origin is not used at all.
    //
    // Compared on scheme, host and port rather than the whole address, because that is the boundary that decides
    // who receives the bearer; a path that moved is the same party, a host that changed is not.
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

    // Whether this token can still be handed to an agent at `moment`, keeping `margin`
    // in hand. The margin is what stops a token that expires in two seconds from being written into a config file that
    // a session will read for the next hour. A token whose server named no expiry is taken at face value — guessing a
    // lifetime would either throw away a working credential or claim one that is already dead.
    public bool IsUsableAt(DateTimeOffset moment, TimeSpan margin) =>
        !string.IsNullOrWhiteSpace(AccessToken) && (ExpiresAt is null || ExpiresAt.Value - margin > moment);
}
