using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Workspaces;

namespace Cockpit.App.Services;

/// <summary>
/// The AI-session panes a start will offer to bring back (AC-410): every <see cref="PaneKind.AiSession"/> pane on
/// a <see cref="WorkspaceType.Sessions"/> workspace. This is the one place that answers "which panes belong to
/// this start" — <c>Program.cs</c> hands the startup worktree reconcile the pane ids from here as its live set (a
/// worktree whose owning session merely has not run yet this run must not read as an orphan, or a restore that
/// still needs it would lose the very worktree it is about to reattach), and the same roster is what a session-state
/// compaction prunes against. Both readers go through <see cref="Panes"/> rather than each walking
/// <see cref="WorkspaceSettings"/> on its own, so the two questions cannot quietly drift apart.
/// </summary>
public static class SessionRestoreRoster
{
    /// <summary>Every AI-session pane on a Sessions workspace in <paramref name="settings"/>, workspace and pane paired.</summary>
    public static IEnumerable<(Workspace Workspace, WorkspacePane Pane)> Panes(WorkspaceSettings settings) =>
        settings.Workspaces
            .Where(workspace => workspace.Type == WorkspaceType.Sessions)
            .SelectMany(workspace => workspace.Panes
                .Where(pane => pane.Kind == PaneKind.AiSession)
                .Select(pane => (workspace, pane)));

    /// <summary>
    /// The pane ids <see cref="Panes"/> would enumerate, read fresh from <paramref name="store"/> — what
    /// <c>Program.cs</c> uses before any view model exists, to hand the startup worktree reconcile and the
    /// session-state compaction the same set.
    /// </summary>
    public static async Task<IReadOnlySet<string>> PaneIdsAsync(IWorkspaceSettingsStore store, CancellationToken cancellationToken = default)
    {
        var settings = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        return Panes(settings).Select(entry => entry.Pane.Id).ToHashSet(StringComparer.Ordinal);
    }
}
