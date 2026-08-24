namespace Cockpit.Core.Abstractions.Agents;

// AC-1013: One message one agent session addressed to another (AC-392) — the envelope, never a bare string, so
// the recipient can present it as data with a stated origin rather than mistake it for something the operator
// asked for. Id is host-minted, not sender-chosen, since the sender has nothing to gain from choosing it.
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

// AC-1013: The result of a delivery attempt. Message is the newly created one on Delivered, the already-waiting
// duplicate on Deduplicated (so the sender gets that id back, not one for a message nobody holds), else null.
public sealed record AgentMessageDelivery(AgentMessageDeliveryOutcome Outcome, AgentMessage? Message);

// AC-1013: One `Drain`'s worth of mail. Remaining must be told to the caller — a capped batch is otherwise
// indistinguishable from an empty inbox, and the tail silently never gets read.
public sealed record AgentInboxBatch(IReadOnlyList<AgentMessage> Messages, int Remaining);

/// <summary>
/// The pending messages agent sessions have addressed to each other (AC-392): what <c>notify</c> writes into and
/// <c>read_inbox</c> drains. Runtime, not durable — the notify trail (<see cref="IAgentNotifyAuditLog"/>) is that
/// record. Keyed on the recipient's pane id alone, not (workspace, pane), since the workspace boundary is enforced upstream in <c>AgentsMcpTools</c> anyway.
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
