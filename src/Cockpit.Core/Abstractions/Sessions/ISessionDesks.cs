using Cockpit.Core.Workspaces;

namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// The desks sessions sit on and the one exclusion every decision over them takes (AC-1439).
/// A frontend that shows the desks owns them; without one the backend keeps them in its own store.
/// </summary>
public interface ISessionDesks
{
    /// <summary>
    /// Runs <paramref name="decision"/> under mutual exclusion with every other change to sessions and desks.
    /// </summary>
    Task<T> RunExclusiveAsync<T>(Func<T> decision);

    /// <summary>
    /// The desks as they stand; read inside <see cref="RunExclusiveAsync{T}"/>.
    /// </summary>
    WorkspaceSettings Workspaces { get; }

    /// <summary>
    /// Whether the desk may be closed at all.
    /// </summary>
    bool CanCloseWorkspace(string workspaceId);

    /// <summary>
    /// Creates a new Sessions desk named <paramref name="name"/>.
    /// </summary>
    Task<Workspace> CreateSessionsWorkspaceAsync(string name);

    /// <summary>
    /// Renames the desk with <paramref name="workspaceId"/>.
    /// </summary>
    Task RenameWorkspaceAsync(string workspaceId, string name);

    /// <summary>
    /// Counts the sessions on the desk and closes it only when that count is zero, in one step.
    /// </summary>
    /// <returns>
    /// The number of sessions found on it; zero means it was closed.
    /// </returns>
    Task<int> CloseWorkspaceIfEmptyAsync(string workspaceId);

    /// <summary>
    /// Writes the record a restart brings <paramref name="pane"/> back from (AC-410).
    /// </summary>
    Task AddPaneAsync(string workspaceId, WorkspacePane pane);

    /// <summary>
    /// Drops the pane's record, so a session that ended on purpose does not come back.
    /// </summary>
    Task RemovePaneAsync(string workspaceId, string paneId);
}
