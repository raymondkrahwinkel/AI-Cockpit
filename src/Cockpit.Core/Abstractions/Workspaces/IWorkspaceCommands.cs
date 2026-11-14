using Cockpit.Core.Workspaces;

namespace Cockpit.Core.Abstractions.Workspaces;

/// <summary>
/// Changes the owning host's workspaces without replacing its saved set or session restore records.
/// </summary>
public interface IWorkspaceCommands
{
    event EventHandler? Changed;
    Task<Workspace> CreateWorkspaceAsync(Workspace workspace);
    Task RenameWorkspaceAsync(string workspaceId, string name);
    Task UpdateWorkspaceAsync(Workspace workspace);
    Task SelectWorkspaceAsync(string workspaceId);
    Task<int> CloseWorkspaceIfEmptyAsync(string workspaceId);
    Task<bool> MoveSessionPaneAsync(string paneId, string targetWorkspaceId);
    Task MoveWorkspaceAsync(string workspaceId, int index);
}
