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
    /// The overlay has to land here rather than at the picker, because everything downstream selects servers
    /// <em>by name</em> out of this catalog: a project-owned server that the catalog does not know is a name the
    /// fan-out cannot resolve, and would silently never reach the session.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<McpServerConfig>> GetServersForProjectAsync(string? projectId, CancellationToken cancellationToken = default);
}
