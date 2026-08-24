namespace Cockpit.Core.Workspaces;

// The one place that answers which `PaneKind` a `WorkspaceType` accepts, kept out of the view models.
// AC-1013: trimmed example — a hand-edited config putting a widget in Sessions is rejected on load by the
// same rule that greys the button — see ticket.
public static class WorkspaceTypeRules
{
    // Whether `kind` may live in a workspace of `type`. Only the two host
    // types hold grid panes; a plugin-registered type owns its whole body and accepts none.
    public static bool Accepts(WorkspaceType type, PaneKind kind)
    {
        if (type == WorkspaceType.Sessions)
        {
            return kind is PaneKind.AiSession or PaneKind.Terminal;
        }

        if (type == WorkspaceType.Dashboard)
        {
            return kind is PaneKind.Widget;
        }

        return false;
    }
}
