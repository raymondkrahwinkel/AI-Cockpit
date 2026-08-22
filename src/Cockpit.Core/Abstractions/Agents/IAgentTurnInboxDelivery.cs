namespace Cockpit.Core.Abstractions.Agents;

/// <summary>
/// Turn-start delivery (AC-394): the seam that lets a session's outgoing turn carry messages waiting for it, so a
/// peer's message arrives without the agent calling <c>read_inbox</c> first. A three-step handshake, not one call,
/// because taking a batch and the turn actually going out are two moments a send can fail between — collapsing them
/// would silently lose messages the sender was already told had arrived. Only panes the host drives through a typed
/// runtime can offer this (a CLI in a pty has no real turn boundary to hang it on); <c>list_agents</c> reports per pane which do.
/// </summary>
public interface IAgentTurnInboxDelivery
{
    /// <summary>
    /// Takes what is waiting for <paramref name="paneId"/> and holds it in flight for the turn about to go out, or
    /// returns null when nothing is waiting. Null rather than an empty notice: a session that never gets mail must
    /// add <em>nothing</em> to its turns, not tokens on every turn of every session, paid by the operator, to say nothing.
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
