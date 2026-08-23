namespace Cockpit.Core.Abstractions.Agents;

/// <summary>
/// Records every <c>notify</c> attempt one agent session makes at another (AC-392), delivered and refused alike, so
/// "who tried to say what to whom" stays answerable from more than memory. Append-only like the consent trail (#AC-47)
/// — no clear or delete; refusals are kept too, since attempts to reach panes on other desks are exactly what you want findable later, invisible if only successes were kept.
/// </summary>
public interface IAgentNotifyAuditLog
{
    /// <summary>
    /// Appends an entry. Never throws: a broken audit log must not take the notify down with it, so a write failure is a logged warning rather than a lost line.
    /// </summary>
    Task RecordAsync(AgentNotifyAuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// The most recent entries, newest first.
    /// </summary>
    Task<IReadOnlyList<AgentNotifyAuditEntry>> ReadRecentAsync(int limit = 200, CancellationToken cancellationToken = default);
}

// What the host did with one `notify` attempt (AC-392).
public enum AgentNotifyOutcome
{
    // The message was accepted and is waiting in the recipient's inbox.
    Accepted,

    // An identical message was already waiting unread, so no second one was added; the sender got the waiting message's id back.
    Deduplicated,

    // The request carried no transport-verified pane, so it could not be attributed to a sender at all.
    RefusedNoVerifiedPane,

    // The addressed pane was not in the caller's own workspace snapshot — either it sits on another desk, or the caller itself resolved to no workspace the cockpit could place it in. Both are the same refusal: the caller cannot address that pane.
    RefusedNotInWorkspace,

    // The caller addressed its own pane.
    RefusedSelf,

    // The addressee, kind or body was missing, or longer than one message may be. The bound is the recipient's
    // protection: the body becomes text in another agent's context, and an unbounded one is both a way to fill host
    // memory and a way to spend a neighbour's whole context window.
    RefusedInvalidContent,

    // The addressed pane was in the caller's workspace when it was checked, but no longer by the time the message had
    // been delivered — its session ended in between. The delivery was taken back rather than left waiting for a pane
    // nobody answers to.
    RefusedRecipientGone,

    // The recipient already holds the most messages one inbox keeps, and has not drained them.
    RefusedRecipientInboxFull,

    // The attempt failed unexpectedly (a race on a closing session, say) and no message was accepted. Recorded so the trail holds every attempt, not only the ones the host reached a verdict on.
    RefusedError,

    // The sender has sent as many messages in the last window as one session may (AC-396), so this one was not
    // accepted. Temporary and per sender: the sender is sending again as soon as its oldest message falls out of
    // the window, and nobody else's sending is affected. Appended after `RefusedError` rather than
    // filed among the other refusals so the numbers already written to the trail keep meaning what they meant.
    RefusedRateLimited,
}

// One line of the agent-notify trail (AC-392).
//
// `At`: When the attempt was handled.
// `Outcome`: What the host did with it — accepted, deduplicated, or the reason it was refused.
// `FromPaneId`: The transport-verified sender, or null when the request carried no verified pane (the one case where there is nobody to name).
// `ToPaneId`: The pane the sender addressed, exactly as given — including one it was not allowed to reach.
// `Kind`: The sender's label for the message, trimmed.
// `Body`: The message text, trimmed: the trail is for recognising an attempt later, not for keeping a second copy of every message.
// `MessageId`: The id of the message now waiting for the recipient, or null when nothing is.
// `Urgent`: Whether the sender asked for the recipient to be woken (AC-395) — what it asked for, kept separate from what it got.
// `Wake`:
// What became of that wake, or null when none was attempted — an ordinary message, or one refused before it
// ever reached the question. The trail is the only place a refused wake is written down: the sender is told, but
// the sender is not who this record is for. Without it the operator can see that agents talked and never that
// one tried to start a turn on another's session.
//
// Defaulted rather than required, and last, so the lines already on disk from before wake existed still read
// back. A trail that stops parsing its own history the day a field is added is not append-only in any sense
// that matters.
public sealed record AgentNotifyAuditEntry(
    DateTimeOffset At,
    AgentNotifyOutcome Outcome,
    string? FromPaneId,
    string ToPaneId,
    string Kind,
    string Body,
    string? MessageId,
    bool Urgent = false,
    AgentWakeOutcome? Wake = null);
