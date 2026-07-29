namespace Cockpit.Core.Mcp;

/// <summary>
/// The credential the cockpit holds for one OAuth-protected MCP server (AC-353), kept so that a single browser
/// sign-in serves every session route and survives a restart of the app.
/// <para>
/// This is deliberately not part of <see cref="McpServerConfig"/>: that record is the operator's own configuration,
/// rewritten in full every time the server is edited, and a token riding along there would be dropped by the first
/// save. What the operator types and what the sign-in yielded have different lifetimes, so they are stored apart.
/// </para>
/// </summary>
public sealed record McpOAuthToken
{
    /// <summary>The access token presented as <c>Authorization: &lt;scheme&gt; &lt;token&gt;</c>.</summary>
    public required string AccessToken { get; init; }

    /// <summary>The authentication scheme the server named, in practice always <c>Bearer</c>.</summary>
    public string Scheme { get; init; } = "Bearer";

    /// <summary>The refresh token, when the authorization server issued one — what lets a stale access token be renewed without asking the operator again.</summary>
    public string? RefreshToken { get; init; }

    /// <summary>When the access token stops being accepted, or <see langword="null"/> if the server named no lifetime.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>The scope the token was granted, as returned by the authorization server.</summary>
    public string? Scope { get; init; }

    /// <summary>The endpoint this token was obtained for. What binds the credential to a host rather than to a name.</summary>
    public string? ResourceUrl { get; init; }

    /// <summary>
    /// The OAuth client ID these tokens were issued to (AC-505). Without this, a refresh token is unusable beyond
    /// the connection that obtained it: the SDK builds a fresh <c>ClientOAuthProvider</c> per connect attempt (a new
    /// session, a renewal, a restart), which starts with no client identity of its own, and it will only try a
    /// refresh grant once it has one to present. Persisting it here is what <c>ClientOAuthProvider</c>'s own
    /// <c>RestoreCachedClientCredentials</c> is built to read back — the mechanism the SDK's own doc comments
    /// describe as letting "a persisted refresh token be used after a restart without re-running dynamic client
    /// registration".
    /// </summary>
    public string? ClientId { get; init; }

    /// <summary>
    /// The OAuth client secret paired with <see cref="ClientId"/>, when dynamic client registration issued one.
    /// A credential in its own right (Iron Law #8) — masked in <see cref="ToString"/> exactly like
    /// <see cref="AccessToken"/> and <see cref="RefreshToken"/>.
    /// </summary>
    public string? ClientSecret { get; init; }

    /// <summary>The token endpoint authentication method negotiated for <see cref="ClientId"/> (e.g. <c>client_secret_post</c>).</summary>
    public string? TokenEndpointAuthMethod { get; init; }

    /// <summary>
    /// The authorization server issuer <see cref="ClientId"/> was registered with. The SDK will not restore a
    /// cached client identity for a different authorization server than the one it is currently talking to — the
    /// same origin discipline <see cref="IsForResource"/> applies to the resource itself.
    /// </summary>
    public string? AuthorizationServer { get; init; }

    /// <summary>
    /// Whether this token may be presented to <paramref name="url"/>. A stored token is found by the server's name,
    /// and a name is not an identity: a project can replace a registry server with its own entry under the same name
    /// and a different address, and the operator can rename or duplicate one. Without this check the credential
    /// obtained for one host would be handed to whatever now answers to that name — so the origin it was issued for
    /// has to match the origin it is about to be sent to, and a token with no recorded origin is not used at all.
    /// <para>
    /// Compared on scheme, host and port rather than the whole address, because that is the boundary that decides
    /// who receives the bearer; a path that moved is the same party, a host that changed is not.
    /// </para>
    /// </summary>
    public bool IsForResource(string? url) =>
        Uri.TryCreate(ResourceUrl, UriKind.Absolute, out var issuedFor)
        && Uri.TryCreate(url, UriKind.Absolute, out var target)
        && string.Equals(issuedFor.Scheme, target.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(issuedFor.Host, target.Host, StringComparison.OrdinalIgnoreCase)
        && issuedFor.Port == target.Port;

    /// <summary>
    /// Overrides the record's generated <c>ToString()</c>, which would otherwise print both tokens in full anywhere
    /// this lands in a log line or an exception message (Iron Law #8) — the same guard <c>PluginMcpServer</c> carries
    /// for the credential it holds.
    /// </summary>
    public override string ToString() =>
        $"{nameof(McpOAuthToken)} {{ {nameof(Scheme)} = {Scheme}, {nameof(AccessToken)} = ***, "
        + $"{nameof(RefreshToken)} = {(string.IsNullOrEmpty(RefreshToken) ? "null" : "***")}, "
        + $"{nameof(ExpiresAt)} = {ExpiresAt}, {nameof(Scope)} = {Scope}, {nameof(ResourceUrl)} = {ResourceUrl}, "
        + $"{nameof(ClientId)} = {ClientId}, {nameof(ClientSecret)} = {(string.IsNullOrEmpty(ClientSecret) ? "null" : "***")}, "
        + $"{nameof(TokenEndpointAuthMethod)} = {TokenEndpointAuthMethod}, {nameof(AuthorizationServer)} = {AuthorizationServer} }}";

    /// <summary>
    /// Whether this token can still be handed to an agent at <paramref name="moment"/>, keeping <paramref name="margin"/>
    /// in hand. The margin is what stops a token that expires in two seconds from being written into a config file that
    /// a session will read for the next hour. A token whose server named no expiry is taken at face value — guessing a
    /// lifetime would either throw away a working credential or claim one that is already dead.
    /// </summary>
    public bool IsUsableAt(DateTimeOffset moment, TimeSpan margin) =>
        !string.IsNullOrWhiteSpace(AccessToken) && (ExpiresAt is null || ExpiresAt.Value - margin > moment);
}
