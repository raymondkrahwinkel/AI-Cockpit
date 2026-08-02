namespace Cockpit.Core.Abstractions.Mcp;

// A cockpit-hosted MCP endpoint (#AC-13, #AC-12) — the one thing you provide to add a new MCP server to Cockpit.
// Register a `CockpitMcpEndpoint` and `CockpitMcpEndpointHost` hosts its tools in-process on a
// loopback address, guarded by this run's auth key (AC-40). It is the cockpit's own server, not the operator's:
// answered live to the session fan-out rather than written to the MCP registry, so the MCP-servers manager never
// lists it. Making a new cockpit MCP is then just "a tools class + a name" — no Kestrel wiring, no registry code.
//
// `ServerName`:
// The server name, how it reaches a session, and how a spawn path can exclude it (as delegation excludes the
// orchestrator for sub-agents), e.g. `cockpit-session`. Unique across endpoints.
// `ToolsType`:
// A class whose `[McpServerTool]` methods are this endpoint's tools. Its constructor dependencies are
// resolved from the application's service provider, so a tool can depend on any registered service.
// `IsEnabled`:
// An optional live gate: when it returns false the endpoint is hosted but not advertised to a session's
// `--mcp-config`, so for an agent the server does not exist (AC-34's master switch). Null means always on.
// `Internal`:
// When true the endpoint is internal-only (AC-204): hidden from every user-facing MCP selection and from the
// no-selection fan-out, yet still mountable when a launch names it explicitly. For a cockpit endpoint only a
// specific spawn should mount (the Autopilot CEO/step tools), never an ordinary operator's to tick.
// `AlwaysMounted`:
// When true the endpoint is hidden from every user-facing MCP selection like `Internal`, but
// mounted into every session regardless of what was selected. For the cockpit's own plumbing that is not a
// choice to weigh up — `cockpit-session`, which is how a session says what it is working on: offering it as
// a tickable server invites unticking it, and the cost of that is a silently missing status line.
public sealed record CockpitMcpEndpoint(
    string ServerName,
    Type ToolsType,
    Func<bool>? IsEnabled = null,
    bool Internal = false,
    bool AlwaysMounted = false);
