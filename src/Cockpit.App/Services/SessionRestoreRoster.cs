using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Assistant;
using Cockpit.Core.Workspaces;

namespace Cockpit.App.Services;

// AC-1013/AC-410: AI-session panes to restore on start; Program.cs uses this live set so a
// not-yet-run worktree isn't misread as orphaned and pruned before it can be reattached.
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

        // AC-1089: live on every start but no workspace pane, so both sweeps read the assistant as gone — releasing its clean worktrees (AC-654 says so for the periodic sweep) and compacting away the id resume needs.
        return Panes(settings).Select(entry => entry.Pane.Id).Append(AssistantIdentity.PaneId).ToHashSet(StringComparer.Ordinal);
    }
}
