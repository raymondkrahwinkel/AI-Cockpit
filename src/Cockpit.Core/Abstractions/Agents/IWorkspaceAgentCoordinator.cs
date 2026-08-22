namespace Cockpit.Core.Abstractions.Agents;

/// <summary>
/// Host-side runtime state for agent-to-agent presence (AC-391): the roster, wake consent, and last contact with the
/// <c>cockpit-agents</c> server. Enrollment and contact are two different facts (AC-613) — used to be one, wrongly
/// reporting an all-night-active pane as absent; <see cref="Enroll"/> notes a known pane, <see cref="RecordContact"/>
/// is the pane reaching in itself, the gap between them being AC-156's silent MCP failure. Keyed on pane id alone
/// since a resolved workspace drifts; wake opt-in (AC-395) lives here too, torn down together by <see cref="Forget"/>.
/// </summary>
public interface IWorkspaceAgentCoordinator
{
    /// <summary>
    /// Puts <paramref name="paneId"/> on the roster because the host knows it is there, whether or not it has ever
    /// called anything (AC-613). Idempotent, and never clears what a pane has already said about itself — a pane
    /// that has agreed to be woken, or made contact, leaves both standing.
    /// </summary>
    void Enroll(string paneId);

    /// <summary>Whether <paramref name="paneId"/> is on the roster — that the host knows of it, not that it has ever called in.</summary>
    bool IsEnrolled(string paneId);

    /// <summary>
    /// Records that <paramref name="paneId"/> has just reached the <c>cockpit-agents</c> server, and enrolls it as
    /// <see cref="Enroll"/> would. The fact <c>list_agents</c> reports a gap on: a known pane that never called in is
    /// either one that hasn't looked yet, has no server mounted, or hit AC-156's silent MCP injection failure — indistinguishable from here.
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
    /// neighbours because it tells a sender whether to wait: "delivered" to a pane that never empties its inbox and
    /// cannot be woken looks just like "delivered" to one that will read it next turn, leaving the sender waiting on
    /// an answer that was never coming. Distinct from <see cref="LastContactUtc"/> — calling <c>list_agents</c> or <c>claim</c> is not collecting mail.
    /// </summary>
    DateTimeOffset? LastInboxReadUtc(string paneId);

    /// <summary>
    /// When a pane the roster knew about was forgotten, or null for a pane id never held (AC-614) — lets a refusal
    /// tell "never existed" (wrong address) apart from "was here and left" (worth having sent to). Deliberately
    /// short-lived and bounded: a courtesy for a sender holding a recent listing's pane id, not a desk history; old departures fall out.
    /// </summary>
    DateTimeOffset? DepartedAtUtc(string paneId);

    /// <summary>
    /// Records this session's own answer about being woken (AC-395), overriding the operator's default for the
    /// session's life; enrolls the pane, leaves contact time alone. No longer where consent lives by default
    /// (AC-615) — a pane used to be unwakeable until it opted in, but an agent will not spend its operator's money on
    /// its own say-so, so nobody ever did. Consent now defaults from <see cref="SetDefaultWakeConsent"/>; this is the per-session override, either direction.
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
    /// Drops <paramref name="paneId"/> from the roster — wake consent and contact time with it — the closing half of
    /// <see cref="Enroll"/>, so an ended session stops being remembered forever. Idempotent — never-enrolled or
    /// already-forgotten panes are a no-op.
    /// </summary>
    void Forget(string paneId);
}
