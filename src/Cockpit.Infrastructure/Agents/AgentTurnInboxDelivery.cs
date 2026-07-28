using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;

namespace Cockpit.Infrastructure.Agents;

/// <summary>
/// Turn-start delivery over the inbox store (AC-394). Thin by intent: the store already knows how to hold a batch
/// in flight and how to put one back, so all that lives here is how much one turn may carry and the fact that
/// nothing waiting means no notice at all.
/// </summary>
internal sealed class AgentTurnInboxDelivery(IAgentMessageInbox inbox) : IAgentTurnInboxDelivery, ISingletonService
{
    /// <summary>
    /// The most messages one turn carries on its own. Deliberately far below <c>read_inbox</c>'s 25: that batch is
    /// one the recipient asked for, on a turn it chose to spend that way, and this one is neither. Here the mail
    /// arrives inside a turn the operator started for their own reasons and is paying for, so an unread backlog
    /// must not be able to bury the thing they actually typed — or turn one neighbour's chattiness into a bill.
    /// <para>
    /// Nothing is lost by the tighter cap, only deferred: the notice says how many are still waiting and names
    /// <c>read_inbox</c>, so a recipient that wants the rest can have it in one call, and the next turn brings the
    /// next few anyway.
    /// </para>
    /// </summary>
    internal const int MaxMessagesPerTurn = 5;

    public AgentInboxTurnNotice? TakeForTurn(string paneId)
    {
        var batch = inbox.TakeForDelivery(paneId, MaxMessagesPerTurn);

        return batch.Messages.Count == 0
            ? null
            : new AgentInboxTurnNotice(paneId, batch.Messages, batch.Remaining);
    }

    public void ConfirmDelivered(AgentInboxTurnNotice notice) =>
        inbox.ConfirmDelivered(notice.PaneId, notice.MessageIds);

    public void ReturnUndelivered(AgentInboxTurnNotice notice) =>
        inbox.ReturnUndelivered(notice.PaneId, notice.MessageIds);
}
