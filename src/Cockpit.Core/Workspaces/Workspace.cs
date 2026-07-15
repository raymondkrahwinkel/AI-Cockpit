namespace Cockpit.Core.Workspaces;

/// <summary>
/// A named, persistent pane layout you switch between via the tab strip above the grid. Immutable: the
/// <c>With…</c> helpers return a new instance and the manager swaps it in, matching how
/// <c>ShortcutSettings</c> and the other settings records in Core behave.
/// </summary>
/// <param name="Id">Stable id, referenced by <c>WorkspaceSettings.ActiveWorkspaceId</c> and never shown.</param>
/// <param name="Name">The tab's label — renamable, and free to collide with another workspace's name.</param>
/// <param name="Type">What this workspace hosts. An invariant, fixed at creation (see <see cref="WorkspaceType"/>).</param>
public sealed record Workspace(string Id, string Name, WorkspaceType Type)
{
    /// <summary>The panes placed in this workspace, in no particular order — <see cref="WorkspacePane.Cell"/> carries the position.</summary>
    public IReadOnlyList<WorkspacePane> Panes { get; init; } = [];

    /// <summary>
    /// The grid settings, meaningful only for <see cref="WorkspaceType.Dashboard"/>. A Sessions workspace
    /// arranges itself with the two overrides below instead.
    /// </summary>
    public DashboardLayout Layout { get; init; } = DashboardLayout.Default;

    /// <summary>
    /// Overrides Options' "show one session at a time" for this workspace; null follows Options (Raymond,
    /// 2026-07-15: "by default volgt die de algemene instellingen, maar overriden per session workspace").
    /// <para>
    /// Null rather than a copy of the global value beside a "use global" flag: two fields that can disagree
    /// eventually will, and then what the desk actually does depends on which one you read. Meaningful only
    /// for <see cref="WorkspaceType.Sessions"/>.
    /// </para>
    /// </summary>
    public bool? SingleSessionLayout { get; init; }

    /// <summary>Overrides Options' "stack sessions vertically" for this workspace; null follows Options. See <see cref="SingleSessionLayout"/>.</summary>
    public bool? StackSessionsVertically { get; init; }

    /// <summary>A new, empty workspace of <paramref name="type"/> with a generated id.</summary>
    public static Workspace Create(string name, WorkspaceType type) =>
        new(Guid.NewGuid().ToString("n"), name, type);

    /// <summary>
    /// This workspace with <paramref name="pane"/> added. Throws when the pane's kind does not belong in this
    /// workspace's type — the invariant is enforced here rather than trusted to every caller, since a pane in
    /// the wrong workspace has no view that can render it.
    /// </summary>
    public Workspace WithPane(WorkspacePane pane)
    {
        if (!WorkspaceTypeRules.Accepts(Type, pane.Kind))
        {
            throw new ArgumentException($"A {Type} workspace cannot hold a {pane.Kind} pane.", nameof(pane));
        }

        return this with { Panes = [.. Panes, pane] };
    }

    /// <summary>This workspace without the pane identified by <paramref name="paneId"/> (a no-op when it holds no such pane).</summary>
    public Workspace WithoutPane(string paneId) =>
        this with { Panes = [.. Panes.Where(pane => pane.Id != paneId)] };

    /// <summary>This workspace with <paramref name="paneId"/> moved to <paramref name="cell"/> (a no-op when it holds no such pane).</summary>
    public Workspace WithPaneMoved(string paneId, GridCell cell) =>
        this with { Panes = [.. Panes.Select(pane => pane.Id == paneId ? pane with { Cell = cell } : pane)] };
}
