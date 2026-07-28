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

    /// <summary>
    /// Five messages is not a bound on size. The sender-facing limit is 2 000 characters of body, but an ampersand
    /// renders as five, so five maximal ampersand bodies are ten times what "five messages" sounds like — arriving
    /// ahead of the sentence the operator typed and is paying for.
    /// </summary>
    [Fact]
    public void TakeForTurn_StopsAtTheCharacterBudget_NotJustTheMessageCount()
    {
        var inbox = new AgentMessageInbox();
        for (var index = 0; index < 5; index++)
        {
            // Distinct bodies so the inbox's own deduplication does not collapse them into one.
            inbox.Deliver("pane-a", "pane-b", "question", new string('&', 1_999) + (char)('a' + index));
        }

        var notice = new AgentTurnInboxDelivery(inbox).TakeForTurn("pane-b");

        Assert.NotNull(notice);
        Assert.True(
            notice.Messages.Count < 5,
            $"five bodies of 2 000 ampersands render as roughly 50 000 characters, well past the "
            + $"{AgentTurnInboxDelivery.MaxRenderedCharsPerTurn}-character budget, yet all five were carried");
        Assert.True(notice.Render().Length <= AgentTurnInboxDelivery.MaxRenderedCharsPerTurn + 2_000);

        // What did not fit is not lost and not read: it is counted as still waiting, and the next turn offers it.
        Assert.Equal(5 - notice.Messages.Count, notice.Remaining);
        Assert.Equal(5 - notice.Messages.Count, inbox.Drain("pane-b", 25).Messages.Count);
    }

    /// <summary>
    /// One message larger than the whole budget still goes. Refusing it would leave it at the head of the queue being
    /// refused again every turn, with everything behind it stuck too — a message that can never arrive and can never
    /// be got past is worse than one oversized turn.
    /// <para>
    /// Delivered straight into the store, because nothing can reach this state through <c>notify</c> today: that path
    /// caps a body at 2 000 characters, which even at the worst escaping expansion renders below the budget. The
    /// branch is a guard on the store's own contract rather than on a reachable input, and it is tested here so that
    /// raising the body cap later cannot silently turn a large message into a permanently stuck one.
    /// </para>
    /// </summary>
    [Fact]
    public void TakeForTurn_CarriesAnOversizedMessageRatherThanStrandingIt()
    {
        var inbox = new AgentMessageInbox();
        inbox.Deliver("pane-a", "pane-b", "question", new string('x', AgentTurnInboxDelivery.MaxRenderedCharsPerTurn + 1_000));
        inbox.Deliver("pane-a", "pane-b", "question", "and a short one behind it");

        var notice = new AgentTurnInboxDelivery(inbox).TakeForTurn("pane-b");

        Assert.NotNull(notice);
        Assert.Single(notice.Messages);
        Assert.Equal(1, notice.Remaining);

        // The one behind it is still there, so the next turn takes it: over budget is deferred, never dropped.
        Assert.Equal("and a short one behind it", Assert.Single(inbox.Drain("pane-b", 25).Messages).Body);
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
