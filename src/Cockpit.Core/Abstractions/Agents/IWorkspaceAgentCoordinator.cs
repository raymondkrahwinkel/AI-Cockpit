namespace Cockpit.Core.Abstractions.Agents;

/// <summary>
/// Host-side runtime state for agent-to-agent presence (AC-391): who is on the roster, whether they have agreed to
/// be woken, and when each last reached the <c>cockpit-agents</c> server itself.
/// <para>
/// <strong>Enrollment and contact are two different facts (AC-613).</strong> They used to be one, and that was the
/// bug: enrollment happened as a side effect of a pane calling a tool, so the roster measured <em>tool use</em> and
/// reported a pane that had worked all night without calling one as absent. Measured in the field on 2026-07-31 —
/// two of three panes showed <c>enrolled: false</c>, one of them demonstrably active the whole time.
/// </para>
/// <para>
/// So <see cref="Enroll"/> is the host writing down a pane it knows about, and <see cref="RecordContact"/> is a pane
/// reaching this server under its own steam. Splitting them keeps the signal that mattered: a pane the host knows
/// about but that has never made contact is the shape of AC-156's silently failed MCP injection, and folding the two
/// back together would either lose that signal or go back to reporting live panes as missing.
/// </para>
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
    /// Puts <paramref name="paneId"/> on the roster because the host knows it is there — an agent session the
    /// cockpit places on a desk, whether or not that session has ever called anything (AC-613). Idempotent, and
    /// deliberately never a way to clear what a pane has already said about itself: calling it for a pane that has
    /// agreed to be woken, or that has made contact, leaves both standing.
    /// </summary>
    void Enroll(string paneId);

    /// <summary>Whether <paramref name="paneId"/> is on the roster — that the host knows of it, not that it has ever called in.</summary>
    bool IsEnrolled(string paneId);

    /// <summary>
    /// Records that <paramref name="paneId"/> has just reached the <c>cockpit-agents</c> server itself, and enrolls
    /// it as <see cref="Enroll"/> would. This is the fact <c>list_agents</c> reports a gap on: a pane the host knows
    /// about that has never called in is either one that simply has not looked yet, one without this server mounted,
    /// or one whose MCP injection failed silently (AC-156) — and from here those cannot be told apart.
    /// </summary>
    void RecordContact(string paneId);

    /// <summary>
    /// When <paramref name="paneId"/> last reached this server, or null when it never has. Null is the gap; it is
    /// not the same as "not on the roster", which after AC-613 only means the host has never seen the pane at all.
    /// </summary>
    DateTimeOffset? LastContactUtc(string paneId);

    /// <summary>
    /// Records whether <paramref name="paneId"/> agrees to be woken — to have a turn started for it, by the host,
    /// on a peer's urgent message (AC-395). Enrolls the pane as any other <c>cockpit-agents</c> call does, and
    /// leaves its contact time alone.
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
    /// Drops <paramref name="paneId"/> from the roster — wake consent and contact time with it — the closing half
    /// of <see cref="Enroll"/>, so a pane whose session ended stops being remembered forever (without this the
    /// roster only ever grows for the lifetime of the app). Idempotent — a pane that was never enrolled, or is
    /// already forgotten, is a no-op.
    /// </summary>
    void Forget(string paneId);
}
