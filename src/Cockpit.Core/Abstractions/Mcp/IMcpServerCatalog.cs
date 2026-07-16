using Cockpit.Core.Mcp;

namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// The effective set of MCP servers a session should see (#26, AC-11): the user-managed registry
/// (<see cref="IMcpServerStore"/>) merged with what the active plugins provide for themselves. The session
/// fan-out (local-model tool loop and the Claude <c>--mcp-config</c>) and the New-session dialog's per-session
/// checklist read from here, so both worlds and the picker see plugin-owned servers alongside registry ones.
/// The MCP-servers <em>manager</em> deliberately does not use this — it edits the registry itself, and a
/// plugin's servers are the plugin's to manage, not the operator's to edit here.
/// </summary>
public interface IMcpServerCatalog
{
    /// <summary>The registry servers plus every active plugin's contributed servers, mapped into the same shape.</summary>
    Task<IReadOnlyList<McpServerConfig>> GetServersAsync(CancellationToken cancellationToken = default);
}
