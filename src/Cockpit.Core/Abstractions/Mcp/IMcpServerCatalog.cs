using Cockpit.Core.Mcp;

namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// The effective set of MCP servers a session should see (#26, AC-11): the registry (<see cref="IMcpServerStore"/>)
/// merged with what active plugins provide. Fan-out and the New-session checklist read from here, so both worlds
/// and the picker see plugin-owned servers alongside registry ones. The MCP-servers <em>manager</em> skips this — it edits the registry itself.
/// </summary>
public interface IMcpServerCatalog
{
    /// <summary>
    /// The registry servers plus every active plugin's contributed servers, mapped into the same shape.
    /// </summary>
    Task<IReadOnlyList<McpServerConfig>> GetServersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The same set from inside <paramref name="projectId"/>: overlay applied, own servers present, turned-off ones
    /// gone. A null or unknown id gives exactly <see cref="GetServersAsync(CancellationToken)"/>. Reaches only the
    /// pickers today: a <em>remove</em> overlay takes full effect, but <em>add or replace</em> does not — fan-out resolves selected names against the unscoped registry, where a project-owned server stays invisible.
    /// </summary>
    Task<IReadOnlyList<McpServerConfig>> GetServersForProjectAsync(string? projectId, CancellationToken cancellationToken = default);
}
