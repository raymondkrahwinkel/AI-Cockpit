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

    // A Sessions workspace and the projects overview, with the sessions one active — so the cockpit still opens
    // on the grid it always opened on, with the overview a tab away rather than something to go and find.
    // One instance, not a fresh one per access. As a getter that called `Workspace.Create` it
    // minted a new id every time it was read — so the view model's default and the store's default were two
    // different workspaces, and a session stamped with one of them belonged to a workspace nothing else knew
    // about. A default has to be the same default every time it is asked for.
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

    // These settings made safe to bind to: at least one workspace, exactly one projects overview, an
    // `ActiveWorkspaceId` that actually resolves, and every dashboard layout clamped. Applied on
    // load, so a hand-edited or truncated `cockpit.json` yields a working cockpit instead of an empty window.
    // The overview is a fixture rather than something to add (Raymond, 2026-07-24): it always exists, exactly
    // once, and cannot be closed. Guaranteeing it here rather than at the view model is what makes that true of
    // every cockpit — an older `cockpit.json` written before it existed, and a hand-edited one that removed
    // it or holds two, all come back with one. It sits at the end of the strip: the first workspace is what a
    // session with no workspace of its own falls back to, and that has to stay a desk which can show one.
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

        return new WorkspaceSettings { Workspaces = ordered, ActiveWorkspaceId = active.Id };
    }

    // These settings with `workspace` appended and made active. Adding a second projects
    // overview is refused — there is one, always, and a second tab onto the same list is not a second desk.
    public WorkspaceSettings WithWorkspace(Workspace workspace) =>
        workspace.Type == WorkspaceType.Projects && Workspaces.Any(existing => existing.Type == WorkspaceType.Projects)
            ? this
            : new() { Workspaces = [.. Workspaces, workspace], ActiveWorkspaceId = workspace.Id };

    // These settings with `workspaceId` removed. Removing the active one selects its
    // neighbour (the next, else the previous), matching how closing a session picks the next selection.
    // Removing the last workspace is refused — a cockpit with no workspace has nothing to show — and so is
    // removing the projects overview, which is a fixture of the cockpit rather than one of the operator's desks.
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

        return new WorkspaceSettings { Workspaces = remaining, ActiveWorkspaceId = active };
    }

    // These settings with `workspace` swapped in by id (a no-op when it holds no such workspace).
    public WorkspaceSettings WithUpdated(Workspace workspace) =>
        this with { Workspaces = [.. Workspaces.Select(existing => existing.Id == workspace.Id ? workspace : existing)] };

    // These settings with `workspaceId` active (a no-op when it holds no such workspace).
    public WorkspaceSettings WithActive(string workspaceId) =>
        _IndexOf(workspaceId) < 0 ? this : this with { ActiveWorkspaceId = workspaceId };

    // These settings with `workspaceId` moved to `targetIndex` in the tab
    // strip, the rest closing the gap behind it. The selection is untouched — reordering rearranges the desks,
    // it does not walk you to a different one. Out-of-range targets are clamped rather than refused, so a drag
    // past either end lands on the end.
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
        return this with { ActiveWorkspaceId = Workspaces[next].Id };
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
