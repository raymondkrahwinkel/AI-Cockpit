namespace Cockpit.Core.Abstractions.Agents;

/// <summary>
/// One message one agent session addressed to another (AC-392) — the envelope, never a bare string. A bare string
/// would arrive in the recipient's context indistinguishable from something the operator asked for; carrying the
/// verified sender and a caller-chosen <c>Kind</c> alongside the text is what lets the recipient
/// present it as data with a stated origin instead of as an instruction.
/// </summary>
/// <param name="Id">The message's own id, minted host-side by <see cref="IAgentMessageInbox"/> — not by the sending agent, which has nothing to gain from choosing it and could otherwise collide with a message it did not send.</param>
/// <param name="FromPaneId">The pane the message actually came from. Stamped from the transport-verified caller, never from anything the sender declared.</param>
/// <param name="ToPaneId">The pane it was addressed to.</param>
/// <param name="Kind">The sender's own label for what this is ("question", "heads-up") — free text, and no more trustworthy than the sender.</param>
/// <param name="Body">The payload text.</param>
/// <param name="SentAtUtc">When the host accepted it.</param>
public sealed record AgentMessage(
    string Id,
    string FromPaneId,
    string ToPaneId,
    string Kind,
    string Body,
    DateTimeOffset SentAtUtc);

/// <summary>What became of one <see cref="IAgentMessageInbox.Deliver"/> call.</summary>
public enum AgentMessageDeliveryOutcome
{
    /// <summary>The message was added to the recipient's inbox.</summary>
    Delivered,

    /// <summary>An identical message was already waiting unread, so nothing was added and the waiting one is returned instead.</summary>
    Deduplicated,

    /// <summary>The recipient already holds the most messages one inbox keeps, so this one was not accepted.</summary>
    RecipientInboxFull,
}

/// <summary>
/// The result of a delivery attempt: what happened, and the message it happened to.
/// </summary>
/// <param name="Outcome">Delivered, deduplicated onto one already waiting, or refused because the recipient's inbox is full.</param>
/// <param name="Message">
/// The message now waiting for the recipient — the newly created one on <see cref="AgentMessageDeliveryOutcome.Delivered"/>,
/// the already-waiting duplicate on <see cref="AgentMessageDeliveryOutcome.Deduplicated"/> (so the sender gets that
/// one's id back rather than an id for a message nobody holds), and null when nothing was accepted.
/// </param>
public sealed record AgentMessageDelivery(AgentMessageDeliveryOutcome Outcome, AgentMessage? Message);

/// <summary>
/// One <see cref="IAgentMessageInbox.Drain"/>'s worth of mail: the messages handed over now, and how many are still
/// waiting behind them.
/// </summary>
/// <param name="Messages">The messages handed to the caller, oldest first. No longer in the inbox — a drain is a handover.</param>
/// <param name="Remaining">
/// How many are still waiting after this batch. Non-zero means the drain was capped and the caller should come back
/// for the rest: the recipient has to be told that, or a bounded batch is indistinguishable from an empty inbox and
/// the tail is silently never read.
/// </param>
public sealed record AgentInboxBatch(IReadOnlyList<AgentMessage> Messages, int Remaining);

/// <summary>
/// The pending messages agent sessions have addressed to each other (AC-392): what <c>notify</c> writes into and
/// <c>read_inbox</c> drains. Runtime state for the life of the app — a message is a note between two live sessions,
/// not something to survive a restart; the durable record of who notified whom is the append-only notify trail
/// (<see cref="IAgentNotifyAuditLog"/>), which is a different thing and deliberately not this.
/// <para>
/// <strong>Keyed on the recipient's pane id alone, not on (workspace, pane).</strong> A pane's workspace is not a
/// property of the pane: it is <em>derived</em>, per call, by <see cref="IWorkspaceAgentGateway"/>, and that answer
/// can change over a pane's life with nothing about the pane itself changing — a session with no explicit workspace
/// falls back to "the first Sessions workspace", and which desk that is changes the moment the operator closes one.
/// State filed under the workspace a pane resolved to at write time is therefore state that can be looked for under
/// a different key at read time: a message delivered on Monday would sit in a partition nobody queries on Tuesday,
/// silently undelivered, with the sender having been told it arrived. Keying on the recipient pane — the identity
/// the transport actually verifies and that does not drift — removes that failure mode rather than making it rarer.
/// </para>
/// <para>
/// The workspace boundary is not weakened by that choice, because this store was never the thing enforcing it. The
/// boundary is enforced at notify time, in <c>AgentsMcpTools</c>: a caller may only address a pane that appears in
/// the snapshot <see cref="IWorkspaceAgentGateway.GetWorkspaceSnapshotAsync"/> returns for the caller's own verified
/// pane, and that snapshot only ever holds panes sharing the caller's workspace. Nothing reaches this store without
/// passing that check first, so nothing here needs to know which workspace a pane is in — and a pane can only ever
/// drain its own inbox, named by the transport rather than by an argument.
/// </para>
/// </summary>
public interface IAgentMessageInbox
{
    /// <summary>
    /// Puts a message from <paramref name="fromPaneId"/> in <paramref name="toPaneId"/>'s inbox, minting its id and
    /// timestamp. When an identical message (same sender, recipient, kind and body) is still waiting unread, no
    /// second copy is added and the waiting one comes back instead — a retried or repeated notify does not fill a
    /// recipient's inbox with the same sentence. A message that is in flight for a turn
    /// (<see cref="TakeForDelivery"/>) counts as still unread for both that check and the per-pane cap: it has not
    /// reached the recipient yet, so treating it as gone would let the same sentence through twice and let a sender
    /// past the cap by timing. Callers must have already established that the two panes may talk; this does not
    /// check, and cannot.
    /// </summary>
    AgentMessageDelivery Deliver(string fromPaneId, string toPaneId, string kind, string body);

    /// <summary>
    /// Takes up to <paramref name="limit"/> of the messages waiting for <paramref name="paneId"/>, oldest first, and
    /// removes them from its inbox. Each message is handed out exactly once — a second call returns nothing unless
    /// something new arrived in between, or the batch was capped and there is more behind it
    /// (<see cref="AgentInboxBatch.Remaining"/>).
    /// <para>
    /// Bounded rather than "everything waiting" because the batch becomes one MCP tool result in the recipient's own
    /// context (AC-392) — and, from AC-394, part of its turn. An inbox at
    /// <c>MaxWaitingPerPane</c> handed over in one go is hundreds of thousands of tokens: a neighbour on the same desk
    /// could spend the recipient's whole context window, and its operator's money, without the recipient ever having
    /// agreed to read that much. The cap is the recipient's protection against its senders, so it is the recipient's
    /// call — the caller of this method — and not a limit a sender can raise.
    /// </para>
    /// </summary>
    /// <param name="paneId">The pane whose inbox to drain — always the transport-verified caller, never a pane id an agent passed.</param>
    /// <param name="limit">The most messages to hand over now. Must be positive; anything else hands over nothing and reports everything as still waiting.</param>
    AgentInboxBatch Drain(string paneId, int limit);

    /// <summary>
    /// Takes up to <paramref name="limit"/> of <paramref name="paneId"/>'s waiting messages the way
    /// <see cref="Drain"/> does, but holds them <em>in flight</em> rather than dropping them: they stop being
    /// waiting — so a second call, and a concurrent <see cref="Drain"/>, cannot hand out the same message again —
    /// and stay held until the caller says which way it went, with <see cref="ConfirmDelivered"/> or
    /// <see cref="ReturnUndelivered"/>.
    /// <para>
    /// This exists because turn-start delivery (AC-394) reads the inbox <em>before</em> the thing that carries the
    /// messages exists. A drain is a handover, and handing over is the last thing that happens to a message: if the
    /// send that was going to carry them then fails, a drained batch is gone, the recipient never saw it, and the
    /// sender was told it arrived. That is the one failure the whole line is built to avoid, so the read had to be
    /// splittable into "taken" and "arrived". <c>read_inbox</c> keeps using <see cref="Drain"/>, where the two are
    /// genuinely the same moment: the messages are in the tool result the agent is already reading.
    /// </para>
    /// </summary>
    /// <param name="paneId">The pane whose inbox to take from — always the transport-verified pane, never one an agent named.</param>
    /// <param name="limit">The most messages to take now. Must be positive; anything else takes nothing and reports everything as still waiting.</param>
    AgentInboxBatch TakeForDelivery(string paneId, int limit);

    /// <summary>
    /// Says the messages <see cref="TakeForDelivery"/> handed over did reach <paramref name="paneId"/>, so they are
    /// dropped for good. Ids that are not in flight for this pane are ignored — a confirmation that arrives twice
    /// says the same thing the second time.
    /// </summary>
    void ConfirmDelivered(string paneId, IReadOnlyList<string> messageIds);

    /// <summary>
    /// Says the messages <see cref="TakeForDelivery"/> handed over did <em>not</em> reach <paramref name="paneId"/>
    /// after all, so they go back to waiting — at the front, keeping their original order, because they are older
    /// than anything that arrived while they were in flight and a message must not lose its place by having been
    /// attempted. Ids that are not in flight for this pane are ignored.
    /// </summary>
    void ReturnUndelivered(string paneId, IReadOnlyList<string> messageIds);

    /// <summary>
    /// Takes one message back out of <paramref name="toPaneId"/>'s inbox, by the id
    /// <see cref="Deliver"/> minted for it. For the caller that has just delivered a message and then found the
    /// delivery should not have stood after all — the recipient's session ended in the window between the workspace
    /// check and the delivery — so that nothing is left behind under a pane id no session answers to any more.
    /// Narrower than <see cref="Forget"/> on purpose: the recipient's other mail was delivered by other senders on
    /// their own merits and is not this caller's to drop.
    /// </summary>
    /// <returns>
    /// True when the message was still waiting and has been removed; false when it was not there — already drained,
    /// already retracted, or in flight for a turn (<see cref="TakeForDelivery"/>), which is past the point where
    /// pulling it back would mean anything: the send that carries it is already under way.
    /// </returns>
    bool Retract(string toPaneId, string messageId);

    /// <summary>
    /// Drops <paramref name="paneId"/>'s inbox unread — for a pane whose session has ended, so undelivered messages
    /// to a session that no longer exists stop being held for the life of the app. Anything in flight for that pane
    /// (<see cref="TakeForDelivery"/>) goes with it: the turn it was riding on belonged to the session that just
    /// ended, so a later <see cref="ReturnUndelivered"/> must not be able to resurrect an inbox under a pane id
    /// nothing answers to. Idempotent; a pane with no inbox is a no-op. Messages this pane <em>sent</em> are not
    /// touched: they belong to their recipients, who are still live and can still read them.
    /// </summary>
    void Forget(string paneId);
}
