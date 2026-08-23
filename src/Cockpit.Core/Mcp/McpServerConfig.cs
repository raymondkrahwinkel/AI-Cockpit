namespace Cockpit.Core.Mcp;

// #26: a user-configured MCP server the cockpit can expose to sessions as tools. One shared registry fans out to
// both the local-LLM tool-loop and the Claude CLI's `--mcp-config`, matching the standard mcpServers shape.
public sealed record McpServerConfig
{
    // Unique display name for the server. A label the operator may change — see `Id` for what identifies it.
    public required string Name { get; init; }

    // AC-403: stable id this server is known by — what the OAuth token store files a token under, so renaming
    // the server does not orphan its sign-in. Empty only for a hand-built config (test/design-time); use
    // `IdentityKey` to key on either way.
    public string Id { get; init; } = string.Empty;

    // The key to file this server's credentials under: `Id` when there is one, otherwise `LegacyIdFor(Name)` —
    // preserving the old name-keyed behaviour so a config without an id doesn't collide on the empty string.
    public string IdentityKey => string.IsNullOrEmpty(Id) ? McpServerIdentity.LegacyIdFor(Name) : Id;

    public McpTransport Transport { get; init; } = McpTransport.Stdio;

    // Which session worlds this server fans out to. Defaults to `McpServerScope.All` so an unscoped server behaves as before.
    public McpServerScope Scope { get; init; } = McpServerScope.All;

    // Executable for a stdio server (e.g. `npx`, `uvx`, a path).
    public string? Command { get; init; }

    // Arguments for the stdio command.
    public IReadOnlyList<string> Args { get; init; } = [];

    // Endpoint URL for an HTTP server.
    public string? Url { get; init; }

    public McpServerAuth Auth { get; init; } = McpServerAuth.None;

    // Static bearer token when `Auth` is `McpServerAuth.ApiKey`.
    public string? ApiKey { get; init; }

    // OAuth authorization-server/discovery base when `Auth` is `McpServerAuth.OAuth`.
    public string? OAuthAuthority { get; init; }

    // OAuth client id when `Auth` is `McpServerAuth.OAuth`.
    public string? OAuthClientId { get; init; }

    // AC-505: explicit OAuth scopes (space-separated), an escape hatch for a server with its own requirements.
    // Set, this overrides the SDK's own derivation (WWW-Authenticate → protected-resource metadata →
    // offline_access); left unset, that derivation runs unchanged.
    public string? OAuthScopes { get; init; }

    // AC-354: extra headers sent alongside whatever `Auth` arranges, for a server needing `X-Api-Key` or another
    // scheme `McpServerAuth.ApiKey` can't express. Additive, not a replacement; ignored for stdio servers.
    public IReadOnlyList<McpHeader> Headers { get; init; } = [];

    // AC-792: TLS certificate fingerprint a paired Cockpit node must present; empty for every other server. A
    // node self-signs, so pinning is what makes the pairing handshake's trust decision stick — without it the
    // shared secret could leak to a man-in-the-middle. Not a credential, so it stays in clear text in cockpit.json.
    public string? PinnedCertificateFingerprint { get; init; }

    // Whether this server is active — a disabled server is kept in the registry but not connected.
    public bool Enabled { get; init; } = true;

    // AC-40: whether the cockpit itself hosts this server on a loopback port. Tells spawn paths to hand the child
    // the app-lifetime auth key; never set for a user-added server, since that key must never reach a third party.
    public bool CockpitHosted { get; init; }

    // AC-204: whether this server is internal-only — kept out of every user-facing MCP selection and the
    // no-selection fan-out, but reachable when a launch names it explicitly (e.g. Autopilot CEO/step endpoints
    // that only a run's own agents scope to). Never set for a user-added server.
    public bool Internal { get; init; }

    // Whether every session gets this server regardless of selection — hidden from pickers like `Internal`, but
    // mounted unconditionally rather than only when named (e.g. `cockpit-session`, so unticking it can't silently
    // cost the operator their status line). Mutually exclusive with `Internal` (opposite arrangement); this wins.
    public bool AlwaysMounted { get; init; }

    // Whether this server is offered only because the session's project points at it (AC-736) — the Depot connection
    // a `depot:` Memory row names. It starts ticked whatever `Projects.ProjectMcpOverlay` says: the project
    // editor's checklist is project-agnostic, so the operator never had a row on which to tick it. Never set for a registry server.
    public bool ProjectLinked { get; init; }

    // Overrides the record's generated `ToString()`, which would otherwise print `ApiKey` in the
    // clear anywhere this lands in a log line or an exception message (Iron Law #8) — the same guard
    // `Cockpit.Plugins.Abstractions.Sessions.PluginMcpServer` carries for the credential it holds.
    public override string ToString() =>
        $"{nameof(McpServerConfig)} {{ {nameof(Name)} = {Name}, {nameof(Transport)} = {Transport}, {nameof(Scope)} = {Scope}, "
        + $"{nameof(Command)} = {Command}, {nameof(Url)} = {Url}, {nameof(Auth)} = {Auth}, "
        + $"{nameof(ApiKey)} = {(string.IsNullOrEmpty(ApiKey) ? "null" : "***")}, "
        + $"{nameof(OAuthAuthority)} = {OAuthAuthority}, {nameof(OAuthClientId)} = {OAuthClientId}, "
        + $"{nameof(OAuthScopes)} = {OAuthScopes}, "
        + $"{nameof(Headers)} = [{string.Join(", ", Headers)}], "
        + $"{nameof(PinnedCertificateFingerprint)} = {PinnedCertificateFingerprint}, "
        + $"{nameof(Enabled)} = {Enabled}, {nameof(CockpitHosted)} = {CockpitHosted}, "
        + $"{nameof(Internal)} = {Internal}, {nameof(AlwaysMounted)} = {AlwaysMounted} }}";
}
