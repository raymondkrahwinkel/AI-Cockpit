namespace Cockpit.Core.Mcp;

/// <summary>
/// A user-configured MCP server the cockpit can expose to sessions as tools (#26). One shared registry
/// fans out to both worlds: the local-LLM driver hosts these servers itself (the agentic tool-loop), and
/// the Claude CLI receives them through its own <c>--mcp-config</c>. Matches the standard
/// <c>{ "mcpServers": { name: { command/args | url/headers } } }</c> shape so that fan-out is a direct map.
/// </summary>
public sealed record McpServerConfig
{
    /// <summary>Unique display name / key for the server.</summary>
    public required string Name { get; init; }

    public McpTransport Transport { get; init; } = McpTransport.Stdio;

    /// <summary>Which session worlds this server fans out to. Defaults to <see cref="McpServerScope.All"/> so an unscoped server behaves as before.</summary>
    public McpServerScope Scope { get; init; } = McpServerScope.All;

    /// <summary>Executable for a stdio server (e.g. <c>npx</c>, <c>uvx</c>, a path).</summary>
    public string? Command { get; init; }

    /// <summary>Arguments for the stdio command.</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>Endpoint URL for an HTTP server.</summary>
    public string? Url { get; init; }

    public McpServerAuth Auth { get; init; } = McpServerAuth.None;

    /// <summary>Static bearer token when <see cref="Auth"/> is <see cref="McpServerAuth.ApiKey"/>.</summary>
    public string? ApiKey { get; init; }

    /// <summary>OAuth authorization-server/discovery base when <see cref="Auth"/> is <see cref="McpServerAuth.OAuth"/>.</summary>
    public string? OAuthAuthority { get; init; }

    /// <summary>OAuth client id when <see cref="Auth"/> is <see cref="McpServerAuth.OAuth"/>.</summary>
    public string? OAuthClientId { get; init; }

    /// <summary>Whether this server is active — a disabled server is kept in the registry but not connected.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Whether the cockpit itself hosts this server on a loopback port (AC-40): the orchestrator and the endpoint-host
    /// servers set this when they publish. It is what tells the spawn paths to hand the child the app-lifetime auth
    /// key for this server, and it is never set for a user-added server — the key is only ever handed to an endpoint
    /// the cockpit runs, never to a third party's.
    /// </summary>
    public bool CockpitHosted { get; init; }

    /// <summary>
    /// Whether this server is internal-only (AC-204): kept out of every user-facing MCP selection (the New-session
    /// checklist, the profile preselection and its token estimate) and out of the no-selection "all enabled servers"
    /// fan-out, yet still reachable when a launch names it explicitly in its per-session selection. It is how a
    /// cockpit-hosted endpoint that only a specific spawn is meant to mount — the Autopilot CEO/step endpoints, which
    /// only a run's own agents scope to by name — stays mountable without an ordinary operator ever seeing or ticking
    /// it. Never set for a user-added server.
    /// </summary>
    public bool Internal { get; init; }

    /// <summary>
    /// Whether every session gets this server whether or not it was selected: kept out of the user-facing pickers
    /// like <see cref="Internal"/>, but mounted regardless of the per-session selection instead of only when named.
    /// It is for the cockpit's own plumbing that is not a choice — <c>cockpit-session</c>, which is how a session
    /// tells the operator what it is working on. Left as an ordinary server it appears in the checklist as
    /// something to weigh up, and unticking it silently costs the operator their status line.
    /// <para>
    /// Mutually exclusive with <see cref="Internal"/>, which is the opposite arrangement (hidden and mounted
    /// <em>only</em> when a launch names it); a server that set both would be asking to be both always and never
    /// mounted, and this one wins. Never set for a user-added server.
    /// </para>
    /// </summary>
    public bool AlwaysMounted { get; init; }
}
