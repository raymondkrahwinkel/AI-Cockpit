namespace Cockpit.Core.Workspaces;

/// <summary>
/// The persisted workspace set and which one is active, under the <c>workspaces</c> section of
/// <c>cockpit.json</c> (same store pattern as layout/shortcuts/voice). Immutable; the <c>With…</c> helpers
/// return a new instance and the store persists it.
/// </summary>
public sealed record WorkspaceSettings
{
    /// <summary>The workspaces, in tab-strip order.</summary>
    public IReadOnlyList<Workspace> Workspaces { get; init; } = [];

    /// <summary>The active workspace's <see cref="Workspace.Id"/>. Null, or an id no workspace carries, resolves to the first one.</summary>
    public string? ActiveWorkspaceId { get; init; }

    /// <summary>
    /// A single Sessions workspace — what an operator who never touched workspaces gets, so the cockpit looks
    /// and behaves exactly as it does today until they add a second one.
    /// </summary>
    public static WorkspaceSettings Default
    {
        get
        {
            var sessions = Workspace.Create("Sessions", WorkspaceType.Sessions);
            return new WorkspaceSettings { Workspaces = [sessions], ActiveWorkspaceId = sessions.Id };
        }
    }

    /// <summary>
    /// The active workspace: the one <see cref="ActiveWorkspaceId"/> names, else the first. Null only when
    /// there are no workspaces at all — which <see cref="Normalized"/> prevents for anything loaded from disk.
    /// </summary>
    public Workspace? Active =>
        Workspaces.FirstOrDefault(workspace => workspace.Id == ActiveWorkspaceId) ?? Workspaces.FirstOrDefault();

    /// <summary>
    /// These settings made safe to bind to: at least one workspace, an <see cref="ActiveWorkspaceId"/> that
    /// actually resolves, and every dashboard layout clamped. Applied on load, so a hand-edited or truncated
    /// <c>cockpit.json</c> yields a working cockpit instead of an empty window.
    /// </summary>
    public WorkspaceSettings Normalized()
    {
        if (Workspaces.Count == 0)
        {
            return Default;
        }

        var clamped = Workspaces.Select(workspace => workspace with { Layout = workspace.Layout.Clamped() }).ToList();
        var active = clamped.FirstOrDefault(workspace => workspace.Id == ActiveWorkspaceId) ?? clamped[0];
        return new WorkspaceSettings { Workspaces = clamped, ActiveWorkspaceId = active.Id };
    }

    /// <summary>These settings with <paramref name="workspace"/> appended and made active.</summary>
    public WorkspaceSettings WithWorkspace(Workspace workspace) =>
        new() { Workspaces = [.. Workspaces, workspace], ActiveWorkspaceId = workspace.Id };

    /// <summary>
    /// These settings with <paramref name="workspaceId"/> removed. Removing the active one selects its
    /// neighbour (the next, else the previous), matching how closing a session picks the next selection.
    /// Removing the last workspace is refused — a cockpit with no workspace has nothing to show.
    /// </summary>
    public WorkspaceSettings WithoutWorkspace(string workspaceId)
    {
        var index = _IndexOf(workspaceId);
        if (index < 0 || Workspaces.Count == 1)
        {
            return this;
        }

        var remaining = Workspaces.Where(workspace => workspace.Id != workspaceId).ToList();
        var active = ActiveWorkspaceId == workspaceId
            ? remaining[Math.Min(index, remaining.Count - 1)].Id
            : ActiveWorkspaceId;

        return new WorkspaceSettings { Workspaces = remaining, ActiveWorkspaceId = active };
    }

    /// <summary>These settings with <paramref name="workspace"/> swapped in by id (a no-op when it holds no such workspace).</summary>
    public WorkspaceSettings WithUpdated(Workspace workspace) =>
        this with { Workspaces = [.. Workspaces.Select(existing => existing.Id == workspace.Id ? workspace : existing)] };

    /// <summary>These settings with <paramref name="workspaceId"/> active (a no-op when it holds no such workspace).</summary>
    public WorkspaceSettings WithActive(string workspaceId) =>
        _IndexOf(workspaceId) < 0 ? this : this with { ActiveWorkspaceId = workspaceId };

    /// <summary>
    /// These settings with the active workspace stepped <paramref name="direction"/> places along the tab
    /// strip, wrapping at both ends — the Ctrl+Shift+Left/Right switch (Raymond, 2026-07-15). Mirrors the
    /// session switch's wrap-around so the two behave the same way on their own axis.
    /// </summary>
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
