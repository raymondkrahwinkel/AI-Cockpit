namespace Cockpit.Core.Abstractions.Agents;

/// <summary>
/// Host-side runtime state for agent-to-agent presence (AC-391): which panes in a workspace have called a
/// <c>cockpit-agents</c> tool at least once — the roster the <c>list_agents</c> tool reads to tell an enrolled
/// pane apart from a gap (a pane the workspace holds but that has never announced itself, which is what a silently
/// failed MCP injection looks like from here — AC-156). State is partitioned per workspace: every operation takes
/// the caller's workspace id and touches only that partition, so nothing here ever lets a pane in one workspace see
/// or affect another's roster.
/// <para>
/// Claims (which agent owns a piece of work) and wake opt-in (which agent wants to be woken for what) are later
/// tickets. This interface deliberately only carries the roster today; the concrete partition is shaped so those can
/// be added beside it without reworking every caller that already keys on workspace id.
/// </para>
/// </summary>
public interface IWorkspaceAgentCoordinator
{
    /// <summary>
    /// Records that <paramref name="paneId"/> has called a <c>cockpit-agents</c> tool in
    /// <paramref name="workspaceId"/>'s roster. Idempotent — calling it again for the same pane changes nothing.
    /// </summary>
    void Enroll(string workspaceId, string paneId);

    /// <summary>Whether <paramref name="paneId"/> is on <paramref name="workspaceId"/>'s roster.</summary>
    bool IsEnrolled(string workspaceId, string paneId);
}
