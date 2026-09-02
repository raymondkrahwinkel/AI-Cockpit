using Cockpit.Core.Abstractions.Agents;
using Cockpit.Infrastructure.Agents;

namespace Cockpit.Infrastructure.Tests.Agents;

/// <summary>
/// The in-flight half of the inbox (AC-394): taking a batch for a turn, and saying afterwards whether that turn
/// carried it. Kept apart from <see cref="AgentMessageInboxTests"/> because what is being held here is a different
/// property — not "a message is handed out once" but "a message is not lost by being attempted".
/// </summary>
public class AgentMessageInboxDeliveryTests
{
    [Fact]
    public void TakeForDelivery_StopsAMessageBeingHandedOutAgainWhileItIsInFlight()
    {
        var inbox = new AgentMessageInbox();
        inbox.Deliver("pane-a", "pane-b", "question", "are you on this?");

        var taken = inbox.TakeForDelivery("pane-b", 5);

        Assert.Single(taken.Messages);

        // Both the other ways out of the inbox see nothing: a second turn starting, and the agent reading its own
        // mail. Either handing it over again would deliver the same sentence twice.
        Assert.Empty(inbox.TakeForDelivery("pane-b", 5).Messages);
        Assert.Empty(inbox.Drain("pane-b", 5).Messages);
    }

    [Fact]
    public void ConfirmDelivered_DropsTheMessagesForGood()
    {
        var inbox = new AgentMessageInbox();
        inbox.Deliver("pane-a", "pane-b", "question", "are you on this?");
        var taken = inbox.TakeForDelivery("pane-b", 5);

        inbox.ConfirmDelivered("pane-b", [.. taken.Messages.Select(message => message.Id)]);

        Assert.Empty(inbox.Drain("pane-b", 5).Messages);
        Assert.Empty(inbox.TakeForDelivery("pane-b", 5).Messages);
    }

    /// <summary>
    /// The reason this half exists. A drain is final, so a send that fails after one would have eaten the message
    /// with the sender already told it arrived.
    /// </summary>
    [Fact]
    public void ReturnUndelivered_PutsTheMessagesBack_SoAFailedTurnCostsNothing()
    {
        var inbox = new AgentMessageInbox();
        inbox.Deliver("pane-a", "pane-b", "question", "are you on this?");
        var taken = inbox.TakeForDelivery("pane-b", 5);

        inbox.ReturnUndelivered("pane-b", [.. taken.Messages.Select(message => message.Id)]);

        var afterwards = inbox.Drain("pane-b", 5);
        Assert.Single(afterwards.Messages);
        Assert.Equal("are you on this?", afterwards.Messages[0].Body);
    }

    [Fact]
    public void ReturnUndelivered_PutsThemBackAheadOfWhatArrivedWhileTheyWereInFlight()
    {
        var inbox = new AgentMessageInbox();
        inbox.Deliver("pane-a", "pane-b", "question", "first");
        inbox.Deliver("pane-a", "pane-b", "question", "second");
        var taken = inbox.TakeForDelivery("pane-b", 2);
        inbox.Deliver("pane-a", "pane-b", "question", "third");

        inbox.ReturnUndelivered("pane-b", [.. taken.Messages.Select(message => message.Id)]);

        // Oldest first, and the two that were attempted keep their own order relative to each other — a message must
        // not lose its place in the queue because a send it happened to ride on failed.
        Assert.Equal(["first", "second", "third"], inbox.Drain("pane-b", 5).Messages.Select(message => message.Body));
    }

    [Fact]
    public void Deliver_StillDeduplicates_AgainstAMessageThatIsInFlight()
    {
        var inbox = new AgentMessageInbox();
        inbox.Deliver("pane-a", "pane-b", "question", "are you on this?");
        inbox.TakeForDelivery("pane-b", 5);

        var again = inbox.Deliver("pane-a", "pane-b", "question", "are you on this?");

        // In flight is not yet read, so a sender repeating itself must not get a second copy through the window
        // between a batch being taken and the turn landing.
        Assert.Equal(AgentMessageDeliveryOutcome.Deduplicated, again.Outcome);
    }

    [Fact]
    public void Deliver_CountsInFlightMessagesTowardTheRecipientsCap()
    {
        var inbox = new AgentMessageInbox();
        for (var index = 0; index < AgentMessageInbox.MaxWaitingPerPane; index++)
        {
            inbox.Deliver("pane-a", "pane-b", "question", $"message {index}");
        }

        // Taking a batch must not free up room: otherwise a sender that watches for the window can push a recipient
        // past a cap that exists to protect it.
        inbox.TakeForDelivery("pane-b", 5);

        Assert.Equal(
            AgentMessageDeliveryOutcome.RecipientInboxFull,
            inbox.Deliver("pane-a", "pane-b", "question", "one more").Outcome);
    }

    [Fact]
    public void Forget_TakesTheInFlightMessagesWithIt()
    {
        var inbox = new AgentMessageInbox();
        inbox.Deliver("pane-a", "pane-b", "question", "are you on this?");
        var taken = inbox.TakeForDelivery("pane-b", 5);

        inbox.Forget("pane-b");
        inbox.ReturnUndelivered("pane-b", [.. taken.Messages.Select(message => message.Id)]);

        // The session those messages were riding a turn for has ended. A return arriving afterwards must not rebuild
        // an inbox under a pane id nothing answers to — that residue is exactly what Forget exists to remove.
        Assert.Empty(inbox.Drain("pane-b", 5).Messages);
    }

    [Fact]
    public void Retract_WillNotTakeBackAMessageThatIsAlreadyOnItsWay()
    {
        var inbox = new AgentMessageInbox();
        var delivered = inbox.Deliver("pane-a", "pane-b", "question", "are you on this?");
        inbox.TakeForDelivery("pane-b", 5);

        Assert.NotNull(delivered.Message);
        Assert.False(inbox.Retract("pane-b", delivered.Message.Id));
    }

    [Fact]
    public void ConfirmDelivered_SaysTheSameThingTwice()
    {
        var inbox = new AgentMessageInbox();
        inbox.Deliver("pane-a", "pane-b", "question", "are you on this?");
        var ids = inbox.TakeForDelivery("pane-b", 5).Messages.Select(message => message.Id).ToArray();

        inbox.ConfirmDelivered("pane-b", ids);
        inbox.ConfirmDelivered("pane-b", ids);

        Assert.Empty(inbox.Drain("pane-b", 5).Messages);
    }

    [Fact]
    public void TakeForDelivery_SaysHowManyAreStillWaitingBehindTheBatch()
    {
        var inbox = new AgentMessageInbox();
        for (var index = 0; index < 8; index++)
        {
            inbox.Deliver("pane-a", "pane-b", "question", $"message {index}");
        }

        var taken = inbox.TakeForDelivery("pane-b", 5);

        Assert.Equal(5, taken.Messages.Count);
        Assert.Equal(3, taken.Remaining);
    }

}
