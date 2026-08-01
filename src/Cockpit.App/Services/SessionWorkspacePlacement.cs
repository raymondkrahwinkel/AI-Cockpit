using Cockpit.App.ViewModels;
using Cockpit.Core.Workspaces;

namespace Cockpit.App.Services;

/// <summary>
/// Which workspace a live session sits on — the one place that decides it (AC-543).
/// </summary>
/// <remarks>
/// <b>Why this exists.</b> The same rule was written out three times, independently:
/// <see cref="WorkspaceAgentGateway"/>'s <c>_ResolveWorkspaceId</c>, <see cref="PaneWorkspaceDirectory"/>'s own
/// inline copy, and <c>CockpitViewModel.BelongsToActiveWorkspace</c>. Each said the same thing — an unassigned
/// session belongs to the first Sessions workspace — and each would have had to be found and taught about the
/// assistant separately. Three copies of a rule is three chances to update two of them.
/// <para>
/// <b>The third session kind.</b> The cockpit had two: a pane in a workspace, and a headless delegated task with
/// an owner pane. The assistant is neither — no pane, no owner, and deliberately no desk. The fallback the two
/// existing kinds rely on is wrong for it: silently landing it in the first Sessions workspace would make it a
/// neighbour on a roster it must not be on, and the failure would surface much later as an agent finding a
/// session it cannot account for. So the assistant resolves to <see langword="null"/> — no workspace at all —
/// and every caller gets that answer explicitly.
/// </para>
/// <para>
/// <see langword="null"/> is not a new case for the callers: it is the answer they already got when a session had
/// no workspace and there was no Sessions workspace to fall back to, and each already refuses rather than
/// inventing an empty desk. The assistant simply always takes that branch, by construction rather than by
/// circumstance.
/// </para>
/// </remarks>
internal static class SessionWorkspacePlacement
{
    /// <summary>
    /// The workspace <paramref name="session"/> sits on, or <see langword="null"/> when it sits on none — the
    /// assistant, or an unassigned session with no Sessions workspace to fall back to.
    /// </summary>
    /// <param name="firstSessionsWorkspaceId">
    /// The fallback desk for an unassigned session, resolved by the caller so a loop over many sessions pays for
    /// that scan once. <see cref="FirstSessionsWorkspaceId"/> is how to get it.
    /// </param>
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

    /// <summary>The desk an unassigned session falls back to: the first Sessions workspace, or <see langword="null"/> when every Sessions desk is closed.</summary>
    public static string? FirstSessionsWorkspaceId(WorkspaceSettings settings) =>
        settings.Workspaces.FirstOrDefault(workspace => workspace.Type == WorkspaceType.Sessions)?.Id;
}
