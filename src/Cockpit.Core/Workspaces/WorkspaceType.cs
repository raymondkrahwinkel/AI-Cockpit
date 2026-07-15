namespace Cockpit.Core.Workspaces;

/// <summary>
/// What a workspace hosts. The type is an invariant, not a hint: it decides which <see cref="PaneKind"/>s
/// may live in the workspace, which "+" affordance and sidebar it shows, and what its empty state says —
/// so a dashboard never offers "New session" and a session workspace never offers "Add widget". Chosen when
/// the workspace is created and not changed afterwards (the panes inside would be invalid under the other
/// type).
/// </summary>
public enum WorkspaceType
{
    /// <summary>Hosts AI sessions and plain terminals — the working context.</summary>
    Sessions,

    /// <summary>Hosts widget panes — the monitoring/at-a-glance context.</summary>
    Dashboard,
}
