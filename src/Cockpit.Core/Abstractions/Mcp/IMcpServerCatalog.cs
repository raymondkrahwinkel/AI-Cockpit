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

    /// <summary>
    /// The same set as seen from inside <paramref name="projectId"/>: that project's overlay applied, so its own
    /// servers are present and the ones it turned off are gone. A null id, or one no project carries, gives
    /// exactly <see cref="GetServersAsync(CancellationToken)"/>.
    /// <para>
    /// What this reaches today is the pickers — the New-session checklist and the project quick start — so an
    /// overlay that <em>removes</em> a server takes full effect: the name is never offered, never selected, and a
    /// launch carries only the names it did select. An overlay that <em>adds or replaces</em> one does not yet: the
    /// session fan-out resolves the selected names against the unscoped registry (<see cref="GetServersAsync"/>),
    /// where a project-owned server does not exist and a project's replacement of a registry server is not seen. A
    /// session-scoped fan-out is what closes that, and until it lands nothing in the app can produce such an
    /// overlay — the project editor only ever switches registry servers off.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<McpServerConfig>> GetServersForProjectAsync(string? projectId, CancellationToken cancellationToken = default);
}
