using Cockpit.Core.Sessions;

namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// Asks every plugin what it gives a session that is starting and returns the merged, scrubbed result (AC-165).
/// Lives here, not the launch routes, because it needs the plugin set and pane's project, both app-side — the
/// same split <see cref="Mcp.IMcpServerCatalog"/> uses: contract in Core, implementation where plugins are.
/// </summary>
public interface ISessionResourceResolver
{
    /// <summary>
    /// What the plugins contribute to the session starting in <paramref name="paneId"/>, or <see
    /// cref="SessionResources.Empty"/> when none has anything (including a project-less pane). Never throws: a
    /// failing plugin is left out and logged — fixable afterwards, unlike a start refused by a misbehaving plugin.
    /// </summary>
    Task<SessionResources> ResolveAsync(string? paneId, CancellationToken cancellationToken = default);
}
