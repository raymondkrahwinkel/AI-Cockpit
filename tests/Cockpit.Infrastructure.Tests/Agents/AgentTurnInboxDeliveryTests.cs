using Cockpit.Core;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Infrastructure.Agents;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Infrastructure.Tests.Agents;

/// <summary>The policy layer over the inbox: how much one turn carries, and that an idle desk produces no notice at all.</summary>
public class AgentTurnInboxDeliveryTests
{
    [Fact]
    public void TakeForTurn_ReturnsNothingAtAll_WhenNoMailIsWaiting()
    {
        var delivery = new AgentTurnInboxDelivery(new AgentMessageInbox());

        // Null, not an empty notice. An empty notice would still be rendered onto every turn of every session that
        // never gets mail, which is the entire cost this design promises not to charge.
        Assert.Null(delivery.TakeForTurn("pane-b"));
    }

    [Fact]
    public void TakeForTurn_CarriesWhatIsWaiting_AndNamesTheRecipient()
    {
        var inbox = new AgentMessageInbox();
        inbox.Deliver("pane-a", "pane-b", "question", "are you on this?");
        var delivery = new AgentTurnInboxDelivery(inbox);

        var notice = delivery.TakeForTurn("pane-b");

        Assert.NotNull(notice);
        Assert.Equal("pane-b", notice.PaneId);
        Assert.Equal("are you on this?", Assert.Single(notice.Messages).Body);
    }

    /// <summary>
    /// The cap is asserted absolutely, not against the constant it is guarding. Written the obvious way — deliver
    /// <c>MaxMessagesPerTurn + 4</c> and expect <c>MaxMessagesPerTurn</c> back — the test moves with the number and
    /// stays green however far the cap is raised, which is the one thing it exists to notice.
    /// </summary>
    [Fact]
    public void TakeForTurn_CarriesAtMostFiveMessages_AndSaysWhatIsLeftBehind()
    {
        var inbox = new AgentMessageInbox();
        for (var index = 0; index < 9; index++)
        {
            inbox.Deliver("pane-a", "pane-b", "question", $"message {index}");
        }

        var notice = new AgentTurnInboxDelivery(inbox).TakeForTurn("pane-b");

        Assert.NotNull(notice);
        Assert.Equal(5, notice.Messages.Count);
        Assert.Equal(4, notice.Remaining);

        // And it stays well under what a read the recipient actually asked for hands over: an unsolicited block on a
        // turn the operator is paying for is not the place to spend a read_inbox-sized batch.
        Assert.True(
            AgentTurnInboxDelivery.MaxMessagesPerTurn < AgentsMcpTools.MaxMessagesPerRead,
            $"a turn carries {AgentTurnInboxDelivery.MaxMessagesPerTurn} messages unasked while read_inbox hands over "
            + $"{AgentsMcpTools.MaxMessagesPerRead} on request — the unasked-for batch must be the smaller of the two");
    }

    [Fact]
    public void ConfirmDelivered_LeavesTheRecipientWithNothingToRead()
    {
        var inbox = new AgentMessageInbox();
        inbox.Deliver("pane-a", "pane-b", "question", "are you on this?");
        var delivery = new AgentTurnInboxDelivery(inbox);
        var notice = delivery.TakeForTurn("pane-b");

        Assert.NotNull(notice);
        delivery.ConfirmDelivered(notice);

        Assert.Empty(inbox.Drain("pane-b", 25).Messages);
    }

    [Fact]
    public void ReturnUndelivered_LeavesTheMailWhereTheNextTurnWillFindIt()
    {
        var inbox = new AgentMessageInbox();
        inbox.Deliver("pane-a", "pane-b", "question", "are you on this?");
        var delivery = new AgentTurnInboxDelivery(inbox);
        var notice = delivery.TakeForTurn("pane-b");

        Assert.NotNull(notice);
        delivery.ReturnUndelivered(notice);

        Assert.NotNull(delivery.TakeForTurn("pane-b"));
    }

    /// <summary>
    /// Registered, and therefore actually reachable. Without this the seam compiles, every test above passes, and no
    /// running session ever gets one — the constructor parameter is optional, so a missing registration is silent.
    /// </summary>
    [Fact]
    public void TurnDelivery_IsRegisteredForTheAppToResolve()
    {
        var services = new ServiceCollection().AddServices(typeof(AgentTurnInboxDelivery).Assembly);

        using var provider = services.BuildServiceProvider();

        Assert.IsType<AgentTurnInboxDelivery>(provider.GetService<IAgentTurnInboxDelivery>());
    }
}
