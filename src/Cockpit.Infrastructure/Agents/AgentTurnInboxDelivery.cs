using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;

namespace Cockpit.Infrastructure.Agents;

// Turn-start delivery over the inbox store (AC-394). Thin by intent: the store already knows how to hold a batch
// in flight and how to put one back, so all that lives here is how much one turn may carry and the fact that
// nothing waiting means no notice at all.
internal sealed class AgentTurnInboxDelivery(IAgentMessageInbox inbox, IWorkspaceAgentCoordinator coordinator)
    : IAgentTurnInboxDelivery, ISingletonService
{
    // AC-1013: far below `read_inbox`'s 25 — this mail rides a turn the operator started and pays for, unasked,
    // so a backlog must not bury what they typed. Nothing lost, only deferred: the notice names `read_inbox`.
    internal const int MaxMessagesPerTurn = 5;

    // AC-1013: most rendered chars a turn carries. A message count alone is exploitable — escaping can expand a
    // 2000-char body fivefold (e.g. ampersands), so 5 messages could render 10x what "5 messages" sounds like.
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

    public void ConfirmDelivered(AgentInboxTurnNotice notice)
    {
        inbox.ConfirmDelivered(notice.PaneId, notice.MessageIds);

        // AC-614: mail reaching this pane counts as its inbox having been read, even though the pane called nothing.
        // Recorded on confirmation rather than when the batch was taken, so what a sender is shown is a delivery
        // that actually happened — a batch that was taken and then returned is not a read.
        coordinator.RecordInboxRead(notice.PaneId);
    }

    public void ReturnUndelivered(AgentInboxTurnNotice notice) =>
        inbox.ReturnUndelivered(notice.PaneId, notice.MessageIds);
}
