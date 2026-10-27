namespace Cockpit.Plugins.Abstractions.Workspaces;

/// <summary>
/// A mark a workspace body puts on its own tab through <see cref="IWorkspaceContext.SetMarker"/>; the host draws it as a dot.
/// </summary>
public enum WorkspaceMarker
{
    /// <summary>The body holds changes that are not saved yet.</summary>
    Unsaved,
}
