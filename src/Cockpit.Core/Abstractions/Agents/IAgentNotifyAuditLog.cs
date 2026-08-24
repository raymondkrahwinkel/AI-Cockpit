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

    // AC-1013: Missing/oversized addressee, kind or body — the bound protects the recipient, since the body
    // becomes text in another agent's context and an unbounded one could fill host memory or a neighbour's context.
    RefusedInvalidContent,

    // The addressed pane was in the caller's workspace when it was checked, but no longer by the time the message had
    // been delivered — its session ended in between. The delivery was taken back rather than left waiting for a pane
    // nobody answers to.
    RefusedRecipientGone,

    // The recipient already holds the most messages one inbox keeps, and has not drained them.
    RefusedRecipientInboxFull,

    // The attempt failed unexpectedly (a race on a closing session, say) and no message was accepted. Recorded so the trail holds every attempt, not only the ones the host reached a verdict on.
    RefusedError,

    // AC-1013: Sender hit its rate limit (AC-396) — temporary and per sender. Appended after RefusedError,
    // not filed among the other refusals, so numbers already on the trail keep meaning what they meant.
    RefusedRateLimited,
}

// AC-1013: One line of the agent-notify trail (AC-392). Wake is the only place a refused wake gets written
// down — the sender is told, but isn't who this record is for. Wake is defaulted and last so pre-wake lines
// on disk still parse; a trail that stops reading its own history isn't append-only in any sense that matters.
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
