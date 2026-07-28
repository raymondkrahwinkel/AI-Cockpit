namespace Cockpit.Core.Abstractions.Agents;

/// <summary>
/// Turn-start delivery (AC-394): the seam that lets a session's outgoing turn carry the messages waiting for it,
/// so a peer's message reaches it without the agent having thought to call <c>read_inbox</c> first.
/// <para>
/// It is a three-step handshake rather than one call, and that is the whole point of it. Taking a batch and the
/// turn actually going out are two moments, and between them a send can fail — so the batch is taken, the turn is
/// attempted, and only then is the inbox told which way it went. Collapsing that into a single "give me the mail"
/// call would put the messages in a string that then never left the host, with the sender already told they had
/// arrived. That silent loss is the failure the whole communication line is built to avoid, and it would be
/// reintroduced here, at the one place that reads the inbox on the recipient's behalf rather than at its request.
/// </para>
/// <para>
/// <strong>Only some panes can offer this.</strong> A session the host drives through a typed runtime has a real
/// turn boundary to hang it on; a session that is a CLI inside a pty does not — there the host writes bytes and
/// the program on the other side decides what a turn is, and text the operator did not type must not carry the
/// Enter that would submit it. Which panes get delivery is therefore not an implementation detail but something
/// <c>list_agents</c> reports per pane, so a sender can see whether its message will arrive on its own.
/// </para>
/// </summary>
public interface IAgentTurnInboxDelivery
{
    /// <summary>
    /// Takes what is waiting for <paramref name="paneId"/> and holds it in flight for the turn about to go out, or
    /// returns null when nothing is waiting.
    /// <para>
    /// Null rather than an empty notice, because the difference is what an idle desk costs. A session that never
    /// gets mail must add <em>nothing</em> to its turns — not a short block saying there is no mail, which would be
    /// tokens on every turn of every session for the lifetime of the app, paid by the operator, to say nothing.
    /// </para>
    /// </summary>
    AgentInboxTurnNotice? TakeForTurn(string paneId);

    /// <summary>Says the turn carrying <paramref name="notice"/> went out, so its messages are read and gone.</summary>
    void ConfirmDelivered(AgentInboxTurnNotice notice);

    /// <summary>
    /// Says the turn carrying <paramref name="notice"/> never went out, so its messages go back to waiting and the
    /// next turn — or a <c>read_inbox</c> — picks them up. What makes a failed send cost nothing.
    /// </summary>
    void ReturnUndelivered(AgentInboxTurnNotice notice);
}
