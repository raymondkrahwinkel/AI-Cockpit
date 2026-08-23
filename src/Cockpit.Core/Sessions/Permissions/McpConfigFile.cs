using Cockpit.Core.Mcp;

namespace Cockpit.Core.Sessions.Permissions;

// The shared rule for which MCP registry servers an agentic CLI session is allowed to see (#26/#44) — building
// the actual spawn config is the provider plugin's job now (AC-353's OAuth/API-key mapping lives there, not
// here). AC-46: the host-side in-process HTTP permission server and its `Serialize(mcpUrl,…)` overloads are gone.
public static class McpConfigFile
{
    // The reserved server key the registry may never claim (it once addressed the cockpit permission server).
    public const string ServerName = "cockpit";

    // Whether a registry server should fan out to an agentic CLI session — enabled, not local-only-scoped
    // (those agents ship their own tools), not the reserved key. The one predicate every fan-out route shares.
    public static bool IsAgentEligible(McpServerConfig server) =>
        server.Enabled
        && server.Scope != McpServerScope.LocalOnly
        && !string.Equals(server.Name, ServerName, StringComparison.OrdinalIgnoreCase);
}
