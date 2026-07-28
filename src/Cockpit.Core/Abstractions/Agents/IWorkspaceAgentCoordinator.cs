namespace Cockpit.Core.Abstractions.Agents;

/// <summary>
/// Host-side runtime state for agent-to-agent presence (AC-391): which panes have called a
/// <c>cockpit-agents</c> tool at least once — the roster the <c>list_agents</c> tool reads to tell an enrolled
/// pane apart from a gap (a pane the workspace holds but that has never announced itself).
/// <para>
/// Keyed on pane id alone — not on (workspace, pane), which an earlier revision of this roster used. A pane's
/// <em>resolved</em> workspace can drift over the pane's own lifetime with nothing about the pane itself
/// changing: an unassigned session falls back to "the first Sessions workspace" (see the gateway that computes
/// <see cref="WorkspaceAgentSnapshot.WorkspaceId"/>), and that fallback answer changes the moment the operator
/// closes whichever desk was first. Partitioning the roster on workspace id stranded such a pane's enrollment in
/// a partition nothing queries anymore the next time it called in, manufacturing a false gap for a perfectly
/// healthy neighbour. The boundary that actually has to hold — a pane in workspace X can never see or affect
/// workspace Y's roster — is already enforced upstream, by <see cref="IWorkspaceAgentGateway"/> only ever
/// including same-workspace panes in the snapshot it hands a caller in the first place; nothing here has to
/// re-enforce it, so nothing here needs to know which workspace a pane is in at all.
/// </para>
/// <para>
/// Claims (which agent owns a piece of work) and wake opt-in are later tickets. Whatever partitioning those need
/// is theirs to design — this roster is not the thing keeping one workspace from seeing another's, so it does
/// not have to share a scheme with them.
/// </para>
/// </summary>
public interface IWorkspaceAgentCoordinator
{
    /// <summary>
    /// Records that <paramref name="paneId"/> has called a <c>cockpit-agents</c> tool. Idempotent — calling it
    /// again for the same pane changes nothing.
    /// </summary>
    void Enroll(string paneId);

    /// <summary>Whether <paramref name="paneId"/> is on the roster.</summary>
    bool IsEnrolled(string paneId);

    /// <summary>
    /// Drops <paramref name="paneId"/> from the roster — the closing half of <see cref="Enroll"/>, so a pane
    /// whose session ended stops being remembered forever (without this the roster only ever grows for the
    /// lifetime of the app). Idempotent — a pane that was never enrolled, or is already forgotten, is a no-op.
    /// </summary>
    void Forget(string paneId);
}
