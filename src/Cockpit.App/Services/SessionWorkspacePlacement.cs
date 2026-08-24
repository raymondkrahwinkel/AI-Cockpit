using Cockpit.App.ViewModels;
using Cockpit.Core.Workspaces;

namespace Cockpit.App.Services;

// AC-543: which workspace a live session sits on — the one place that decides it, replacing three independent
// copies of the same rule. The assistant is a third session kind (no pane, no owner) that must resolve to `null`
// rather than the first-Sessions-workspace fallback, so it never appears as a neighbour on a roster it must not be on.
internal static class SessionWorkspacePlacement
{
    // The workspace `session` sits on, or `null` for the assistant or an unassigned session with no fallback.
    // `firstSessionsWorkspaceId` is the fallback desk, resolved by the caller so a loop over many sessions pays
    // for that scan once — see `FirstSessionsWorkspaceId`.
    public static string? Resolve(SessionPanelViewModel session, string? firstSessionsWorkspaceId)
    {
        // First, and unconditionally: the assistant has no desk, and no fallback may give it one. Ahead of the
        // WorkspaceId check as well as the fallback, so it holds even if something later stamps one on anyway.
        if (session.BelongsToNoWorkspace)
        {
            return null;
        }

        return session.WorkspaceId.Length > 0 ? session.WorkspaceId : firstSessionsWorkspaceId;
    }

    // The desk an unassigned session falls back to: the first Sessions workspace, or `null` when every Sessions desk is closed.
    public static string? FirstSessionsWorkspaceId(WorkspaceSettings settings) =>
        settings.Workspaces.FirstOrDefault(workspace => workspace.Type == WorkspaceType.Sessions)?.Id;
}
