namespace Cockpit.Core.Mcp;

// One mounted endpoint's live network-reachable address (AC-790) — what the operator copies into a second
// Cockpit's "add MCP server" dialog. Absent (never constructed) for an endpoint while node binding is off.
public sealed record NodeEndpointAddress(string ServerName, string Url);
