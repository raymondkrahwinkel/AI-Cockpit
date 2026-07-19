namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// A cockpit-hosted MCP endpoint (#AC-13, #AC-12) — the one thing you provide to add a new MCP server to Cockpit.
/// Register a <see cref="CockpitMcpEndpoint"/> and <c>CockpitMcpEndpointHost</c> hosts its tools in-process on a
/// loopback address, guarded by this run's auth key (AC-40). It is the cockpit's own server, not the operator's:
/// answered live to the session fan-out rather than written to the MCP registry, so the MCP-servers manager never
/// lists it. Making a new cockpit MCP is then just "a tools class + a name" — no Kestrel wiring, no registry code.
/// </summary>
/// <param name="ServerName">
/// The server name, how it reaches a session, and how a spawn path can exclude it (as delegation excludes the
/// orchestrator for sub-agents), e.g. <c>cockpit-session</c>. Unique across endpoints.
/// </param>
/// <param name="ToolsType">
/// A class whose <c>[McpServerTool]</c> methods are this endpoint's tools. Its constructor dependencies are
/// resolved from the application's service provider, so a tool can depend on any registered service.
/// </param>
/// <param name="IsEnabled">
/// An optional live gate: when it returns false the endpoint is hosted but not advertised to a session's
/// <c>--mcp-config</c>, so for an agent the server does not exist (AC-34's master switch). Null means always on.
/// </param>
public sealed record CockpitMcpEndpoint(string ServerName, Type ToolsType, Func<bool>? IsEnabled = null);
