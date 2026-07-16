namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// A cockpit-hosted MCP endpoint (#AC-13, #AC-12) — the one thing you provide to add a new MCP server to Cockpit.
/// Register a <see cref="CockpitMcpEndpoint"/> and <c>CockpitMcpEndpointHost</c> hosts its tools in-process on a
/// loopback address and auto-publishes it to the shared MCP registry as its own entry: a separate MCP server that
/// shows up in the New-session picker and is tickable per session, alongside the registry's other servers. Making
/// a new cockpit MCP is then just "a tools class + a name" — no Kestrel wiring, no registry code, per endpoint.
/// </summary>
/// <param name="ServerName">
/// The registry name and how it appears in the MCP-servers picker (e.g. <c>cockpit-session</c>). Unique across
/// endpoints; it is also the name a spawn path can exclude (as delegation excludes the orchestrator for sub-agents).
/// </param>
/// <param name="ToolsType">
/// A class whose <c>[McpServerTool]</c> methods are this endpoint's tools. Its constructor dependencies are
/// resolved from the application's service provider, so a tool can depend on any registered service.
/// </param>
/// <param name="EnabledByDefault">
/// Whether the endpoint is on for new sessions by default. Re-asserted enabled on every launch (the same
/// on-by-default rule the orchestrator uses), so a stale disabled entry never silently turns it off.
/// </param>
public sealed record CockpitMcpEndpoint(string ServerName, Type ToolsType, bool EnabledByDefault = true);
