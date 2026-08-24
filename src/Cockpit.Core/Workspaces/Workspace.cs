namespace Cockpit.Core.Workspaces;

// A named, persistent pane layout you switch between via the tab strip. Immutable, like `ShortcutSettings`.
// AC-1013: trimmed per-field docs for `Id` (stable, never shown), `Name` (renamable, may collide), `Type`
// (fixed at creation) — see ticket for full text.
public sealed record Workspace(string Id, string Name, WorkspaceType Type)
{
    // The panes placed in this workspace, in no particular order — `WorkspacePane.Cell` carries the position.
    public IReadOnlyList<WorkspacePane> Panes { get; init; } = [];

    // The grid settings, meaningful only for `WorkspaceType.Dashboard`. A Sessions workspace
    // arranges itself with the two overrides below instead.
    public DashboardLayout Layout { get; init; } = DashboardLayout.Default;

    // Overrides Options' "show one session at a time" for this workspace; null follows Options (Raymond, 2026-07-15).
    // AC-1013: trimmed rationale — null rather than a copy-plus-flag, since two fields that can disagree eventually
    // will; meaningful only for `WorkspaceType.Sessions` — see ticket.
    public bool? SingleSessionLayout { get; init; }

    // Overrides Options' "stack sessions vertically" for this workspace; null follows Options. See `SingleSessionLayout`.
    public bool? StackSessionsVertically { get; init; }

    // Overrides Options' focus+rail layout (AC-444) for this workspace; null follows Options. See `SingleSessionLayout`.
    public bool? FocusRailLayout { get; init; }

    // Overrides the global focus/rail divider weight (AC-443) for this workspace; null follows the
    // global default. See `SingleSessionLayout`.
    public double? FocusRailWeight { get; init; }

    // A new, empty workspace of `type` with a generated id.
    public static Workspace Create(string name, WorkspaceType type) =>
        new(Guid.NewGuid().ToString("n"), name, type);

    // This workspace with `pane` added. Throws when the pane's kind does not belong in this
    // workspace's type — the invariant is enforced here rather than trusted to every caller, since a pane in
    // the wrong workspace has no view that can render it.
    public Workspace WithPane(WorkspacePane pane)
    {
        if (!WorkspaceTypeRules.Accepts(Type, pane.Kind))
        {
            throw new ArgumentException($"A {Type} workspace cannot hold a {pane.Kind} pane.", nameof(pane));
        }

        return this with { Panes = [.. Panes, pane] };
    }

    // This workspace without the pane identified by `paneId` (a no-op when it holds no such pane).
    public Workspace WithoutPane(string paneId) =>
        this with { Panes = [.. Panes.Where(pane => pane.Id != paneId)] };

    // This workspace with `paneId` moved to `cell` (a no-op when it holds no such pane).
    public Workspace WithPaneMoved(string paneId, GridCell cell) =>
        this with { Panes = [.. Panes.Select(pane => pane.Id == paneId ? pane with { Cell = cell } : pane)] };

    // This workspace with `paneId`'s `Title`/`NameIsChosen` updated (no-op if absent) — a name that changes
    // after the pane already exists (AC-514): an operator's inline rename, or a name a plugin/agent suggested.
    // AC-1013: trimmed — replaces the pane in place like `WithPaneMoved`, unlike `WithPane`'s append; see ticket.
    public Workspace WithPaneRenamed(string paneId, string title, bool nameIsChosen) =>
        this with { Panes = [.. Panes.Select(pane => pane.Id == paneId ? pane with { Title = title, NameIsChosen = nameIsChosen } : pane)] };
}
