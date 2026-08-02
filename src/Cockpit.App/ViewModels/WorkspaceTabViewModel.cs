using CommunityToolkit.Mvvm.ComponentModel;
using Material.Icons;
using Cockpit.Core.Workspaces;

namespace Cockpit.App.ViewModels;

// One tab in the workspace strip. Mostly a snapshot — the strip is rebuilt whenever the workspace set or the
// selection changes — but it carries its own inline-rename state, the same way a session row does, since a
// rename lives and dies inside the tab rather than in a dialog.
public sealed partial class WorkspaceTabViewModel(Workspace workspace, bool isActive, MaterialIconKind? icon = null) : ObservableObject
{
    public string Id => workspace.Id;

    public bool IsActive => isActive;

    // The icon that tells the workspace kinds apart at a glance in the strip: a plugin type's own registered icon
    // when it has one, else the host icon for a built-in workspace, and a neutral plugin mark for a plugin type
    // that registered no vector icon.
    public MaterialIconKind Icon =>
        icon
        ?? (workspace.Type == WorkspaceType.Dashboard ? MaterialIconKind.ViewDashboardOutline
            : workspace.Type == WorkspaceType.Sessions ? MaterialIconKind.ChatOutline
            : workspace.Type == WorkspaceType.Projects ? MaterialIconKind.FolderMultipleOutline
            : MaterialIconKind.PuzzleOutline);

    // The tab's label. Set on commit so the strip updates before the rebuilt tabs arrive from the store.
    [ObservableProperty]
    private string _name = workspace.Name;

    // True while the tab shows its edit box instead of its label.
    [ObservableProperty]
    private bool _isRenaming;

    // The editable name, seeded from `Name` when the rename starts.
    [ObservableProperty]
    private string _editName = workspace.Name;

    // Starts an inline rename, seeding the editable name from the current one.
    public void BeginRename()
    {
        EditName = Name;
        IsRenaming = true;
    }

    // Ends the inline rename and reports the name to commit, or null when there is nothing to do (blank, or
    // unchanged). The caller persists it — the tab is a view over a stored record and does not write.
    public string? CommitRename()
    {
        IsRenaming = false;
        var trimmed = EditName?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed == Name)
        {
            return null;
        }

        Name = trimmed;
        return trimmed;
    }

    // Cancels the inline rename, discarding the edit.
    public void CancelRename() => IsRenaming = false;
}
