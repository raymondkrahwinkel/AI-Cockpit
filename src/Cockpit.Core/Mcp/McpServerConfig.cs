namespace Cockpit.Core.Mcp;

// A user-configured MCP server the cockpit can expose to sessions as tools (#26). One shared registry
// fans out to both worlds: the local-LLM driver hosts these servers itself (the agentic tool-loop), and
// the Claude CLI receives them through its own `--mcp-config`. Matches the standard
// `{ "mcpServers": { name: { command/args | url/headers } } }` shape so that fan-out is a direct map.
public sealed record McpServerConfig
{
    // Unique display name for the server. A label the operator may change — see `Id` for what identifies it.
    public required string Name { get; init; }

    // The stable id this server is known by (AC-403) — what the OAuth token store files a token under, so that
    // renaming the server in the dialog does not leave its sign-in behind under the old name. Empty only for a
    // config built by hand (a test, a design-time view); everything that reads one off disk or off a plugin
    // contribution carries an id, and `IdentityKey` is what to key on either way.
    public string Id { get; init; } = string.Empty;

    // The key to file this server's credentials under: `Id` when there is one, and otherwise the id
    // the name derives to (`McpServerIdentity.LegacyIdFor`) — which is exactly the name-keyed
    // behaviour that came before, so a config assembled without an id behaves as it always did rather than
    // keying on the empty string alongside every other such config.
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

    // Explicit OAuth scopes (space-separated) when `Auth` is `McpServerAuth.OAuth` — the
    // escape hatch for a server with its own requirements (AC-505). Set, this overrides the scope the SDK would
    // otherwise derive on its own (WWW-Authenticate challenge → protected-resource metadata → offline_access when
    // the authorization server advertises it); left unset, that derivation runs unchanged.
    public string? OAuthScopes { get; init; }

    // Extra headers sent to an HTTP server alongside whatever `Auth` arranges (AC-354), for a server
    // that wants `X-Api-Key` or another scheme `McpServerAuth.ApiKey` cannot express. An addition
    // rather than a replacement: the ordinary bearer case stays a single field the operator does not have to spell
    // out. Ignored for a stdio server, which has no request to put them on.
    public IReadOnlyList<McpHeader> Headers { get; init; } = [];

    // Whether this server is active — a disabled server is kept in the registry but not connected.
    public bool Enabled { get; init; } = true;

    // Whether the cockpit itself hosts this server on a loopback port (AC-40): the orchestrator and the endpoint-host
    // servers set this when they publish. It is what tells the spawn paths to hand the child the app-lifetime auth
    // key for this server, and it is never set for a user-added server — the key is only ever handed to an endpoint
    // the cockpit runs, never to a third party's.
    public bool CockpitHosted { get; init; }

    // Whether this server is internal-only (AC-204): kept out of every user-facing MCP selection (the New-session
    // checklist, the profile preselection and its token estimate) and out of the no-selection "all enabled servers"
    // fan-out, yet still reachable when a launch names it explicitly in its per-session selection. It is how a
    // cockpit-hosted endpoint that only a specific spawn is meant to mount — the Autopilot CEO/step endpoints, which
    // only a run's own agents scope to by name — stays mountable without an ordinary operator ever seeing or ticking
    // it. Never set for a user-added server.
    public bool Internal { get; init; }

    // Whether every session gets this server whether or not it was selected: kept out of the user-facing pickers
    // like `Internal`, but mounted regardless of the per-session selection instead of only when named.
    // It is for the cockpit's own plumbing that is not a choice — `cockpit-session`, which is how a session
    // tells the operator what it is working on. Left as an ordinary server it appears in the checklist as
    // something to weigh up, and unticking it silently costs the operator their status line.
    //
    // Mutually exclusive with `Internal`, which is the opposite arrangement (hidden and mounted
    // *only* when a launch names it); a server that set both would be asking to be both always and never
    // mounted, and this one wins. Never set for a user-added server.
    public bool AlwaysMounted { get; init; }

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
        + $"{nameof(Enabled)} = {Enabled}, {nameof(CockpitHosted)} = {CockpitHosted}, "
        + $"{nameof(Internal)} = {Internal}, {nameof(AlwaysMounted)} = {AlwaysMounted} }}";
}
