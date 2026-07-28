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

    [Fact]
    public void TakeForTurn_CarriesFewerThanReadInboxWould_AndSaysWhatIsLeftBehind()
    {
        var inbox = new AgentMessageInbox();
        for (var index = 0; index < AgentTurnInboxDelivery.MaxMessagesPerTurn + 4; index++)
        {
            inbox.Deliver("pane-a", "pane-b", "question", $"message {index}");
        }

        var notice = new AgentTurnInboxDelivery(inbox).TakeForTurn("pane-b");

        Assert.NotNull(notice);
        Assert.Equal(AgentTurnInboxDelivery.MaxMessagesPerTurn, notice.Messages.Count);
        Assert.Equal(4, notice.Remaining);
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
