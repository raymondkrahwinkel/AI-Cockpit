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
    /// recipient's inbox with the same sentence. Callers must have already established that the two panes may talk;
    /// this does not check, and cannot.
    /// </summary>
    AgentMessageDelivery Deliver(string fromPaneId, string toPaneId, string kind, string body);

    /// <summary>
    /// Takes everything waiting for <paramref name="paneId"/> and empties its inbox, oldest first. Each message is
    /// handed out exactly once — a second call returns nothing unless something new arrived in between.
    /// </summary>
    IReadOnlyList<AgentMessage> Drain(string paneId);

    /// <summary>
    /// Drops <paramref name="paneId"/>'s inbox unread — for a pane whose session has ended, so undelivered messages
    /// to a session that no longer exists stop being held for the life of the app. Idempotent; a pane with no inbox
    /// is a no-op. Messages this pane <em>sent</em> are not touched: they belong to their recipients, who are still
    /// live and can still read them.
    /// </summary>
    void Forget(string paneId);
}
