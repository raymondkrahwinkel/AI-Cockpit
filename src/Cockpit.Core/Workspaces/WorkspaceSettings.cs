namespace Cockpit.Core.Workspaces;

// The persisted workspace set and which one is active, under the `workspaces` section of
// `cockpit.json` (same store pattern as layout/shortcuts/voice). Immutable; the `With…` helpers
// return a new instance and the store persists it.
public sealed record WorkspaceSettings
{
    // The workspaces, in tab-strip order.
    public IReadOnlyList<Workspace> Workspaces { get; init; } = [];

    // The active workspace's `Workspace.Id`. Null, or an id no workspace carries, resolves to the first one.
    public string? ActiveWorkspaceId { get; init; }

    // AC-1306: the workspace nodes the panels sidebar's session tree shows collapsed. Here rather than on
    // `Workspace` because it is one strip's view state, not part of what a desk is — and here rather than in a
    // store of its own, since it has to survive a restart alongside the desks it names.
    public IReadOnlyList<string> CollapsedSidebarWorkspaceIds { get; init; } = [];

    // A Sessions workspace and the projects overview, sessions active. One instance, not minted fresh per access.
    // AC-1013: trimmed — a getter calling `Workspace.Create` minted a new id per read, so view-model and store
    // defaults diverged — see ticket.
    public static WorkspaceSettings Default { get; } = _CreateDefault();

    private static WorkspaceSettings _CreateDefault()
    {
        var sessions = Workspace.Create("Sessions", WorkspaceType.Sessions);
        return new WorkspaceSettings
        {
            Workspaces = [sessions, _CreateProjects()],
            ActiveWorkspaceId = sessions.Id,
        };
    }

    private static Workspace _CreateProjects() => Workspace.Create("Projects", WorkspaceType.Projects);

    // The active workspace: the one `ActiveWorkspaceId` names, else the first. Null only when
    // there are no workspaces at all — which `Normalized` prevents for anything loaded from disk.
    public Workspace? Active =>
        Workspaces.FirstOrDefault(workspace => workspace.Id == ActiveWorkspaceId) ?? Workspaces.FirstOrDefault();

    // These settings made safe to bind to: one workspace, one projects overview, a resolving `ActiveWorkspaceId`.
    // AC-1013: trimmed — overview is a fixture (Raymond, 2026-07-24), always exactly one, un-closable, enforced
    // here not the view model, placed last as the no-workspace fallback — see ticket.
    public WorkspaceSettings Normalized()
    {
        if (Workspaces.Count == 0)
        {
            return Default;
        }

        var clamped = Workspaces.Select(workspace => workspace with { Layout = workspace.Layout.Clamped() }).ToList();

        // Extra overviews are dropped rather than renamed apart: the type holds no panes, so a second one carries
        // nothing to lose, and two tabs showing the same list is exactly what "exactly once" rules out.
        var overviews = clamped.Where(workspace => workspace.Type == WorkspaceType.Projects).ToList();
        var ordered = clamped.Where(workspace => workspace.Type != WorkspaceType.Projects).ToList();
        var overview = overviews.Count > 0 ? overviews[0] : _CreateProjects();
        ordered.Add(overview);

        // An operator sitting on an overview that was one of several stays on the one that survived, rather than
        // being walked to whichever desk happens to be first: the tab they were on still exists, under a different id.
        var wasOnADroppedOverview = overviews.Skip(1).Any(dropped => dropped.Id == ActiveWorkspaceId);
        var active = wasOnADroppedOverview
            ? overview
            : ordered.FirstOrDefault(workspace => workspace.Id == ActiveWorkspaceId) ?? ordered[0];

        // Collapsed ids of desks that are gone are dropped here rather than carried forever: a reused id would
        // otherwise open a brand-new desk collapsed.
        return (this with
        {
            Workspaces = ordered,
            CollapsedSidebarWorkspaceIds = [.. CollapsedSidebarWorkspaceIds.Where(id => ordered.Any(workspace => workspace.Id == id))],
        }).WithActive(active.Id);
    }

    // These settings with `workspace` appended and made active. Adding a second projects
    // overview is refused — there is one, always, and a second tab onto the same list is not a second desk.
    public WorkspaceSettings WithWorkspace(Workspace workspace) =>
        workspace.Type == WorkspaceType.Projects && Workspaces.Any(existing => existing.Type == WorkspaceType.Projects)
            ? this
            : (this with { Workspaces = [.. Workspaces, workspace] }).WithActive(workspace.Id);

    // These settings with `workspaceId` removed. Removing the active one selects its neighbour (next, else
    // previous), matching how closing a session picks the next selection. Removing the last workspace, or the
    // AC-1013: trimmed — projects overview, is refused; overview is a fixture, not one of the operator's desks — see ticket.
    public WorkspaceSettings WithoutWorkspace(string workspaceId)
    {
        var index = _IndexOf(workspaceId);
        if (index < 0 || Workspaces.Count == 1 || Workspaces[index].Type == WorkspaceType.Projects)
        {
            return this;
        }

        var remaining = Workspaces.Where(workspace => workspace.Id != workspaceId).ToList();
        var active = ActiveWorkspaceId == workspaceId
            ? remaining[Math.Min(index, remaining.Count - 1)].Id
            : ActiveWorkspaceId;

        return (this with
        {
            Workspaces = remaining,
            CollapsedSidebarWorkspaceIds = [.. CollapsedSidebarWorkspaceIds.Where(id => id != workspaceId)],
        }).WithActive(active ?? remaining[0].Id);
    }

    // These settings with `workspace` swapped in by id (a no-op when it holds no such workspace).
    public WorkspaceSettings WithUpdated(Workspace workspace) =>
        this with { Workspaces = [.. Workspaces.Select(existing => existing.Id == workspace.Id ? workspace : existing)] };

    // These settings with `workspaceId` active (a no-op when it holds no such workspace). Every route that walks
    // to another desk goes through here — the tab strip, the sidebar tree, the Ctrl+Shift+arrows and the add/close
    // helpers above — so the two rules below hold wherever the operator switched from.
    public WorkspaceSettings WithActive(string workspaceId) =>
        _IndexOf(workspaceId) < 0
            ? this
            : this with
            {
                ActiveWorkspaceId = workspaceId,
                // AC-1306: walking to a desk opens its node in the sidebar tree. A collapsed active node hides
                // exactly the sessions just switched to, so the tree would stop pointing at where you are.
                CollapsedSidebarWorkspaceIds = [.. CollapsedSidebarWorkspaceIds.Where(id => id != workspaceId)],
            };

    // AC-1306: these settings with `workspaceId`'s sidebar node collapsed or expanded. Collapsing is the one
    // gesture that does not walk anywhere — see the chevron in `CockpitView.axaml`.
    public WorkspaceSettings WithSidebarCollapsed(string workspaceId, bool collapsed) =>
        _IndexOf(workspaceId) < 0 || collapsed == CollapsedSidebarWorkspaceIds.Contains(workspaceId)
            ? this
            : this with
            {
                CollapsedSidebarWorkspaceIds = collapsed
                    ? [.. CollapsedSidebarWorkspaceIds, workspaceId]
                    : [.. CollapsedSidebarWorkspaceIds.Where(id => id != workspaceId)],
            };

    // These settings with `workspaceId` moved to `targetIndex` in the tab strip, closing the gap behind it.
    // Selection is untouched — reordering rearranges the desks, it does not walk you to a different one.
    // Out-of-range targets are clamped rather than refused, so a drag past either end lands on the end.
    public WorkspaceSettings WithMoved(string workspaceId, int targetIndex)
    {
        var from = _IndexOf(workspaceId);
        if (from < 0 || Workspaces.Count <= 1)
        {
            return this;
        }

        var to = Math.Clamp(targetIndex, 0, Workspaces.Count - 1);
        if (to == from)
        {
            return this;
        }

        var reordered = Workspaces.ToList();
        reordered.RemoveAt(from);
        reordered.Insert(to, Workspaces[from]);
        return this with { Workspaces = reordered };
    }

    // These settings with the active workspace stepped `direction` places along the tab
    // strip, wrapping at both ends — the Ctrl+Shift+Left/Right switch (Raymond, 2026-07-15). Mirrors the
    // session switch's wrap-around so the two behave the same way on their own axis.
    public WorkspaceSettings WithSteppedActive(int direction)
    {
        if (Workspaces.Count <= 1 || direction == 0)
        {
            return this;
        }

        var current = Math.Max(0, _IndexOf(Active?.Id ?? string.Empty));
        var next = ((current + direction) % Workspaces.Count + Workspaces.Count) % Workspaces.Count;
        return WithActive(Workspaces[next].Id);
    }

    private int _IndexOf(string workspaceId)
    {
        for (var index = 0; index < Workspaces.Count; index++)
        {
            if (Workspaces[index].Id == workspaceId)
            {
                return index;
            }
        }

        return -1;
    }
}
