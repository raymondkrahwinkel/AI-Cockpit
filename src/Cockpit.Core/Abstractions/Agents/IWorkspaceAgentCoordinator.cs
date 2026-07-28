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
/// Claims (which agent owns a piece of work) went to their own store: a claim has content — a resource, an owner,
/// a time — and a shape of its own. Wake opt-in (AC-395) did not, and lives here. It is the same key, the same
/// one-bit answer and, above all, the same lifetime as enrollment: a pane's consent to be woken has to die with
/// the pane, and <see cref="Forget"/> is already the one call every teardown path makes. A fourth store for one
/// boolean would have meant a fourth line at each of those call sites, which is the kind of addition that gets
/// made at one of them and forgotten at the other — leaving a standing permission to wake a session that no
/// longer exists.
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
    /// Records whether <paramref name="paneId"/> agrees to be woken — to have a turn started for it, by the host,
    /// on a peer's urgent message (AC-395). Enrolls the pane as any other <c>cockpit-agents</c> call does.
    /// <para>
    /// The opt-in <em>is</em> the consent, so it is only ever set by the pane it is about: a session says this
    /// about itself and about nothing else. Off until said otherwise — a pane that has never called this is a
    /// pane that has not agreed, and silence must never read as agreement for something that spends the
    /// operator's money on a turn they did not ask for.
    /// </para>
    /// </summary>
    void SetWakeConsent(string paneId, bool consents);

    /// <summary>
    /// Whether <paramref name="paneId"/> has agreed to be woken. False for a pane that never said, and false for
    /// one that has been forgotten — consent does not outlive the session that gave it.
    /// </summary>
    bool HasWakeConsent(string paneId);

    /// <summary>
    /// Drops <paramref name="paneId"/> from the roster, wake consent included — the closing half of
    /// <see cref="Enroll"/>, so a pane whose session ended stops being remembered forever (without this the
    /// roster only ever grows for the lifetime of the app). Idempotent — a pane that was never enrolled, or is
    /// already forgotten, is a no-op.
    /// </summary>
    void Forget(string paneId);
}
