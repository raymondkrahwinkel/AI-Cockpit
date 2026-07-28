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

    /// <summary>
    /// The most rendered characters a turn carries, counted on the text that actually goes out.
    /// <para>
    /// A count of messages is not a bound on size, and the gap between the two is a sender's to exploit: the
    /// sender-facing limit is 2 000 characters of body, but escaping expands what is stored, and an ampersand
    /// expands fivefold — so five bodies of 2 000 ampersands each are ten times what "five messages" sounds like,
    /// arriving ahead of the sentence the operator actually typed and paid for. Budgeting on rendered size closes
    /// that. It is the recipient's bound, like the count, and not one a sender can raise.
    /// </para>
    /// </summary>
    internal const int MaxRenderedCharsPerTurn = 12_000;

    public AgentInboxTurnNotice? TakeForTurn(string paneId)
    {
        var batch = inbox.TakeForDelivery(paneId, MaxMessagesPerTurn);
        if (batch.Messages.Count == 0)
        {
            return null;
        }

        var carried = new List<AgentMessage>();
        var overBudget = new List<string>();
        var spent = 0;

        foreach (var message in batch.Messages)
        {
            var cost = AgentInboxTurnNotice.RenderedCostOf(message);

            // The first message always goes, however large it is. Refusing it on size would leave it at the head of
            // the queue being refused again on every turn, blocking everything behind it forever — a message the
            // recipient can never receive and never get past is worse than one oversized turn.
            if (carried.Count > 0 && spent + cost > MaxRenderedCharsPerTurn)
            {
                overBudget.Add(message.Id);
                continue;
            }

            carried.Add(message);
            spent += cost;
        }

        if (overBudget.Count > 0)
        {
            // Straight back to waiting, at the front, keeping their order: they were never carried, so they are not
            // read, and the next turn offers them again.
            inbox.ReturnUndelivered(paneId, overBudget);
        }

        return new AgentInboxTurnNotice(paneId, carried, batch.Remaining + overBudget.Count);
    }

    public void ConfirmDelivered(AgentInboxTurnNotice notice) =>
        inbox.ConfirmDelivered(notice.PaneId, notice.MessageIds);

    public void ReturnUndelivered(AgentInboxTurnNotice notice) =>
        inbox.ReturnUndelivered(notice.PaneId, notice.MessageIds);
}
