using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Workspaces;

namespace Cockpit.App.ViewModels;

/// <summary>
/// One tab in the workspace strip. Mostly a snapshot — the strip is rebuilt whenever the workspace set or the
/// selection changes — but it carries its own inline-rename state, the same way a session row does, since a
/// rename lives and dies inside the tab rather than in a dialog.
/// </summary>
public sealed partial class WorkspaceTabViewModel(Workspace workspace, bool isActive) : ObservableObject
{
    public string Id => workspace.Id;

    public bool IsActive => isActive;

    /// <summary>The glyph that tells the two workspace kinds apart at a glance in the strip.</summary>
    public string Icon => workspace.Type == WorkspaceType.Dashboard ? "📊" : "💬";

    /// <summary>The tab's label. Set on commit so the strip updates before the rebuilt tabs arrive from the store.</summary>
    [ObservableProperty]
    private string _name = workspace.Name;

    /// <summary>True while the tab shows its edit box instead of its label.</summary>
    [ObservableProperty]
    private bool _isRenaming;

    /// <summary>The editable name, seeded from <see cref="Name"/> when the rename starts.</summary>
    [ObservableProperty]
    private string _editName = workspace.Name;

    /// <summary>Starts an inline rename, seeding the editable name from the current one.</summary>
    public void BeginRename()
    {
        EditName = Name;
        IsRenaming = true;
    }

    /// <summary>
    /// Ends the inline rename and reports the name to commit, or null when there is nothing to do (blank, or
    /// unchanged). The caller persists it — the tab is a view over a stored record and does not write.
    /// </summary>
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

    /// <summary>Cancels the inline rename, discarding the edit.</summary>
    public void CancelRename() => IsRenaming = false;
}
