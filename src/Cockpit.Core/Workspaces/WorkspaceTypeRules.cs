namespace Cockpit.Core.Workspaces;

/// <summary>
/// The one place that answers which <see cref="PaneKind"/> a <see cref="WorkspaceType"/> accepts. Kept as a
/// rule rather than spread over the view models so the "+" affordance, the add-pane paths and the
/// persistence loader all agree — a config hand-edited to put a widget in a Sessions workspace is rejected
/// on load by the same rule that greys the button.
/// </summary>
public static class WorkspaceTypeRules
{
    /// <summary>Whether <paramref name="kind"/> may live in a workspace of <paramref name="type"/>.</summary>
    public static bool Accepts(WorkspaceType type, PaneKind kind) => type switch
    {
        WorkspaceType.Sessions => kind is PaneKind.AiSession or PaneKind.Terminal,
        WorkspaceType.Dashboard => kind is PaneKind.Widget,
        _ => false,
    };
}
