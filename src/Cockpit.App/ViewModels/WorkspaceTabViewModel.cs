using Cockpit.Core.Workspaces;

namespace Cockpit.App.ViewModels;

/// <summary>
/// One tab in the workspace strip. A snapshot rather than a live wrapper: the strip is rebuilt whenever the
/// workspace set or the selection changes, which keeps the tab free of change-tracking for a record that is
/// itself immutable.
/// </summary>
public sealed class WorkspaceTabViewModel(Workspace workspace, bool isActive)
{
    public string Id => workspace.Id;

    public string Name => workspace.Name;

    public bool IsActive => isActive;

    /// <summary>The glyph that tells the two workspace kinds apart at a glance in the strip.</summary>
    public string Icon => workspace.Type == WorkspaceType.Dashboard ? "📊" : "💬";
}
