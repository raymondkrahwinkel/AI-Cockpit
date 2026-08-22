namespace Cockpit.Core.Abstractions.Agents;

// One message one agent session addressed to another (AC-392) — the envelope, never a bare string. A bare string
// would arrive in the recipient's context indistinguishable from something the operator asked for; carrying the
// verified sender and a caller-chosen `Kind` alongside the text is what lets the recipient
// present it as data with a stated origin instead of as an instruction.
//
// `Id`: The message's own id, minted host-side by `IAgentMessageInbox` — not by the sending agent, which has nothing to gain from choosing it and could otherwise collide with a message it did not send.
// `FromPaneId`: The pane the message actually came from. Stamped from the transport-verified caller, never from anything the sender declared.
// `ToPaneId`: The pane it was addressed to.
// `Kind`: The sender's own label for what this is ("question", "heads-up") — free text, and no more trustworthy than the sender.
// `Body`: The payload text.
// `SentAtUtc`: When the host accepted it.
public sealed record AgentMessage(
    string Id,
    string FromPaneId,
    string ToPaneId,
    string Kind,
    string Body,
    DateTimeOffset SentAtUtc);

// What became of one `IAgentMessageInbox.Deliver` call.
public enum AgentMessageDeliveryOutcome
{
    // The message was added to the recipient's inbox.
    Delivered,

    // An identical message was already waiting unread, so nothing was added and the waiting one is returned instead.
    Deduplicated,

    // The recipient already holds the most messages one inbox keeps, so this one was not accepted.
    RecipientInboxFull,
}

// The result of a delivery attempt: what happened, and the message it happened to.
//
// `Outcome`: Delivered, deduplicated onto one already waiting, or refused because the recipient's inbox is full.
// `Message`:
// The message now waiting for the recipient — the newly created one on `AgentMessageDeliveryOutcome.Delivered`,
// the already-waiting duplicate on `AgentMessageDeliveryOutcome.Deduplicated` (so the sender gets that
// one's id back rather than an id for a message nobody holds), and null when nothing was accepted.
public sealed record AgentMessageDelivery(AgentMessageDeliveryOutcome Outcome, AgentMessage? Message);

// One `IAgentMessageInbox.Drain`'s worth of mail: the messages handed over now, and how many are still
// waiting behind them.
//
// `Messages`: The messages handed to the caller, oldest first. No longer in the inbox — a drain is a handover.
// `Remaining`:
// How many are still waiting after this batch. Non-zero means the drain was capped and the caller should come back
// for the rest: the recipient has to be told that, or a bounded batch is indistinguishable from an empty inbox and
// the tail is silently never read.
public sealed record AgentInboxBatch(IReadOnlyList<AgentMessage> Messages, int Remaining);

/// <summary>
/// The pending messages agent sessions have addressed to each other (AC-392): what <c>notify</c> writes into and
/// <c>read_inbox</c> drains. Runtime, not durable — the append-only notify trail (<see cref="IAgentNotifyAuditLog"/>)
/// is that record. Keyed on the recipient's pane id alone, not (workspace, pane), because a pane's derived workspace
/// drifts, and because the workspace boundary is enforced upstream in <c>AgentsMcpTools</c> anyway.
/// </summary>
public interface IAgentMessageInbox
{
    /// <summary>
    /// Puts a message from <paramref name="fromPaneId"/> in <paramref name="toPaneId"/>'s inbox, minting its id and
    /// timestamp. An identical still-unread message is not duplicated — the waiting one comes back instead, and one
    /// in flight (<see cref="TakeForDelivery"/>) still counts as unread. Callers must have already established the panes may talk.
    /// </summary>
    AgentMessageDelivery Deliver(string fromPaneId, string toPaneId, string kind, string body);

    /// <summary>
    /// Takes up to <paramref name="limit"/> waiting messages for <paramref name="paneId"/>, oldest first, removing
    /// them once each (see <see cref="AgentInboxBatch.Remaining"/>). Bounded because a full <c>MaxWaitingPerPane</c>
    /// inbox is hundreds of thousands of tokens a neighbour could spend without consent (AC-392/AC-394), so the cap is the recipient's call, never a sender's.
    /// </summary>
    AgentInboxBatch Drain(string paneId, int limit);

    /// <summary>
    /// The oldest message waiting for <paramref name="paneId"/>, without taking it — for a caller that only needs
    /// to know whether there is unread mail (AC-656's inbox-linked wake). Null when nothing is waiting; a message
    /// already in flight (<see cref="TakeForDelivery"/>) does not count, since something already took it.
    /// </summary>
    AgentMessage? PeekOldest(string paneId);

    /// <summary>
    /// Takes up to <paramref name="limit"/> of <paramref name="paneId"/>'s waiting messages like <see cref="Drain"/>,
    /// but holds them <em>in flight</em> until the caller reports which way it went via <see cref="ConfirmDelivered"/>
    /// or <see cref="ReturnUndelivered"/> — needed because turn-start delivery (AC-394) reads the inbox before the send that carries them exists, so a failed send must not leave the sender told "arrived" for a batch nobody saw.
    /// </summary>
    AgentInboxBatch TakeForDelivery(string paneId, int limit);

    /// <summary>
    /// Says the messages <see cref="TakeForDelivery"/> handed over did reach <paramref name="paneId"/>, so they are
    /// dropped for good. Ids that are not in flight for this pane are ignored — a confirmation that arrives twice
    /// says the same thing the second time.
    /// </summary>
    void ConfirmDelivered(string paneId, IReadOnlyList<string> messageIds);

    /// <summary>
    /// Says the messages <see cref="TakeForDelivery"/> handed over did <em>not</em> reach <paramref name="paneId"/>,
    /// so they go back to waiting at the front, keeping original order, since they must not lose their place for
    /// having been attempted. Ids not in flight for this pane are ignored.
    /// </summary>
    void ReturnUndelivered(string paneId, IReadOnlyList<string> messageIds);

    /// <summary>
    /// Takes one message back out of <paramref name="toPaneId"/>'s inbox by the id <see cref="Deliver"/> minted for
    /// it — for a caller whose just-delivered message's recipient session ended before delivery. Narrower than
    /// <see cref="Forget"/>: other senders' mail is not this caller's to drop. False when already drained, retracted, or in flight.
    /// </summary>
    bool Retract(string toPaneId, string messageId);

    /// <summary>
    /// Drops <paramref name="paneId"/>'s inbox unread — for an ended session, so messages stop being held forever.
    /// Anything in flight (<see cref="TakeForDelivery"/>) goes with it, so <see cref="ReturnUndelivered"/> cannot
    /// resurrect an inbox nothing answers to. Idempotent. Messages this pane <em>sent</em> are untouched.
    /// </summary>
    void Forget(string paneId);
}
