using Cockpit.Core.Mcp;

namespace Cockpit.Core.Sessions.Permissions;

/// <summary>
/// The shared rule for which MCP registry servers an agentic CLI session (Claude Code, Codex) is allowed to see
/// (#26/#44). Actually building a session's <c>--mcp-config</c>/spawn config from the eligible set is not this
/// type's job any more: the SDK route (<see cref="Cockpit.Infrastructure.Sessions.PluginSessionDriverAdapter"/>)
/// and the interactive-TTY route (<see cref="Cockpit.Infrastructure.Sessions.Tty.PluginTtySessionProviderAdapter"/>)
/// each resolve the registry against this predicate and hand the result to the provider plugin, which builds its
/// own spawn config (e.g. <c>ClaudeMcpConfig</c>/<c>CodexMcpConfig</c>) — the provider plugins carry the OAuth/API-key
/// credential mapping AC-353 added, which a host-side JSON serializer never grew (AC-380: it had no production
/// caller and was removed).
/// <para>
/// The cockpit once injected its own in-process HTTP permission server into a config built here; that endpoint is
/// gone, and the host-side <c>Serialize(mcpUrl,…)</c> overloads that carried it were removed with it (AC-46) so an
/// unauthenticated permission endpoint cannot be reintroduced through a stale config path.
/// </para>
/// </summary>
public static class McpConfigFile
{
    /// <summary>The reserved server key the registry may never claim (it once addressed the cockpit permission server).</summary>
    public const string ServerName = "cockpit";

    /// <summary>
    /// Whether a registry server should fan out to an agentic CLI session (Claude Code, Codex) — enabled, not
    /// scoped to local models only (those agents ship their own file/shell/web tools, so a filesystem server
    /// there is noise), and not the reserved key (never surfaced from the registry). The one predicate every
    /// fan-out route (#26/#44) shares, so "which servers a coding agent sees" lives in one place.
    /// </summary>
    public static bool IsAgentEligible(McpServerConfig server) =>
        server.Enabled
        && server.Scope != McpServerScope.LocalOnly
        && !string.Equals(server.Name, ServerName, StringComparison.OrdinalIgnoreCase);
}
