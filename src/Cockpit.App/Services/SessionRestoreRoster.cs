using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Workspaces;

namespace Cockpit.App.Services;

// The AI-session panes a start will offer to bring back (AC-410): every `PaneKind.AiSession` pane on
// a `WorkspaceType.Sessions` workspace. This is the one place that answers "which panes belong to
// this start" — `Program.cs` hands the startup worktree reconcile the pane ids from here as its live set (a
// worktree whose owning session merely has not run yet this run must not read as an orphan, or a restore that
// still needs it would lose the very worktree it is about to reattach), and the same roster is what a session-state
// compaction prunes against. Both readers go through `Panes` rather than each walking
// `WorkspaceSettings` on its own, so the two questions cannot quietly drift apart.
public static class SessionRestoreRoster
{
    // Every AI-session pane on a Sessions workspace in `settings`, workspace and pane paired.
    public static IEnumerable<(Workspace Workspace, WorkspacePane Pane)> Panes(WorkspaceSettings settings) =>
        settings.Workspaces
            .Where(workspace => workspace.Type == WorkspaceType.Sessions)
            .SelectMany(workspace => workspace.Panes
                .Where(pane => pane.Kind == PaneKind.AiSession)
                .Select(pane => (workspace, pane)));

    // The pane ids `Panes` would enumerate, read fresh from `store` — what
    // `Program.cs` uses before any view model exists, to hand the startup worktree reconcile and the
    // session-state compaction the same set.
    public static async Task<IReadOnlySet<string>> PaneIdsAsync(IWorkspaceSettingsStore store, CancellationToken cancellationToken = default)
    {
        var settings = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        return Panes(settings).Select(entry => entry.Pane.Id).ToHashSet(StringComparer.Ordinal);
    }
}
