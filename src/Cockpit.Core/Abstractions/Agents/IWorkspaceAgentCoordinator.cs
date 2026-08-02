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
    /// Records that <paramref name="paneId"/> has just collected its mail — by calling <c>read_inbox</c>, or by
    /// having a batch confirmed as carried out with one of its turns (AC-394). Counts as contact too.
    /// </summary>
    void RecordInboxRead(string paneId);

    /// <summary>
    /// When <paramref name="paneId"/> last collected mail, or null when it never has (AC-614). Reported to
    /// neighbours because it is what tells a sender whether to wait: "delivered" to a pane that has never emptied
    /// its inbox and cannot be woken looks exactly like "delivered" to one that will read it on its next turn, and
    /// in the first case the sender waits for an answer that was never coming.
    /// <para>
    /// Distinct from <see cref="LastContactUtc"/>: a pane can call <c>list_agents</c> or <c>claim</c> all day
    /// without ever collecting a message.
    /// </para>
    /// </summary>
    DateTimeOffset? LastInboxReadUtc(string paneId);

    /// <summary>
    /// When a pane the roster knew about was forgotten, or null for a pane id it has never held (AC-614). This is
    /// what lets a refusal tell "this pane never existed" apart from "this pane was here and has gone" — which for
    /// a sender are different situations: the first is a wrong address, the second is a recipient that left, and
    /// only the second means the message was worth sending.
    /// <para>
    /// Deliberately short-lived and bounded: it is a courtesy for a sender holding a pane id from a listing it took
    /// a few minutes ago, not a history of the desk. Old departures fall out.
    /// </para>
    /// </summary>
    DateTimeOffset? DepartedAtUtc(string paneId);

    /// <summary>
    /// Records this session's own answer about being woken (AC-395), overriding the operator's default for as long
    /// as the session lives. Enrolls the pane as any other <c>cockpit-agents</c> call does, and leaves its contact
    /// time alone.
    /// <para>
    /// This is no longer where the consent lives (AC-615). It used to be: a pane was unwakeable until it said
    /// otherwise, on the reasoning that a wake spends the operator's turn and only the pane could speak for that.
    /// The reasoning was right and the placement was wrong — an agent will not spend its operator's money on its
    /// own say-so, so no pane ever opted in, and the wake route was built and never used. The consent moved to a
    /// setting the operator sets (<see cref="SetDefaultWakeConsent"/>); this stayed, as the per-session deviation
    /// from it, in either direction.
    /// </para>
    /// </summary>
    void SetWakeConsent(string paneId, bool consents);

    /// <summary>
    /// Sets what a session that has not answered for itself is taken to have agreed to — the operator's setting
    /// (AC-615). Applies to every pane that has not called <see cref="SetWakeConsent"/>, at once and live, so
    /// turning it off stops wakes for panes that are already running rather than only for ones started afterwards.
    /// </summary>
    void SetDefaultWakeConsent(bool consents);

    /// <summary>
    /// Whether <paramref name="paneId"/> may be woken: its own answer if it gave one, and the operator's default
    /// otherwise. False for a pane that has been forgotten — a session that has ended is not a session anything can
    /// start a turn on.
    /// </summary>
    bool HasWakeConsent(string paneId);

    /// <summary>
    /// Whether <paramref name="paneId"/>'s answer is its own rather than the operator's default — what
    /// <c>set_wake_optin</c> tells a caller back, so an agent can see whether it is looking at its own decision or
    /// at one made for it.
    /// </summary>
    bool HasOwnWakeConsent(string paneId);

    /// <summary>
    /// Drops <paramref name="paneId"/> from the roster — wake consent and contact time with it — the closing half
    /// of <see cref="Enroll"/>, so a pane whose session ended stops being remembered forever (without this the
    /// roster only ever grows for the lifetime of the app). Idempotent — a pane that was never enrolled, or is
    /// already forgotten, is a no-op.
    /// </summary>
    void Forget(string paneId);
}
