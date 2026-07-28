using Cockpit.Core.Abstractions.Agents;
using Cockpit.Infrastructure.Agents;

namespace Cockpit.Infrastructure.Tests.Agents;

/// <summary>
/// The inbox store on its own (AC-392), independent of the MCP tools that drive it: it is keyed on the recipient's
/// pane id alone, dedups an identical message that is still waiting, hands each message over exactly once, and
/// gives a closing session's unread mail back rather than holding it for the life of the app. It deliberately does
/// <em>not</em> know about workspaces — the boundary is enforced at notify time by <c>AgentsMcpTools</c> against
/// the gateway's snapshot, so there is nothing to prove about isolation here.
/// </summary>
public sealed class AgentMessageInboxTests
{
    [Fact]
    public void Deliver_ThenDrain_HandsBackTheWholeEnvelope()
    {
        var inbox = new AgentMessageInbox();

        var delivery = inbox.Deliver("pane-a", "pane-b", "question", "who owns the parser?");

        Assert.Equal(AgentMessageDeliveryOutcome.Delivered, delivery.Outcome);
        var waiting = Assert.Single(inbox.Drain("pane-b"));
        Assert.Equal(delivery.Message!.Id, waiting.Id);
        Assert.Equal("pane-a", waiting.FromPaneId);
        Assert.Equal("pane-b", waiting.ToPaneId);
        Assert.Equal("question", waiting.Kind);
        Assert.Equal("who owns the parser?", waiting.Body);
        Assert.True(waiting.SentAtUtc > DateTimeOffset.MinValue);
    }

    [Fact]
    public void Deliver_MintsADistinctIdPerMessage()
    {
        var inbox = new AgentMessageInbox();

        var first = inbox.Deliver("pane-a", "pane-b", "note", "one");
        var second = inbox.Deliver("pane-a", "pane-b", "note", "two");

        Assert.NotEqual(first.Message!.Id, second.Message!.Id);
    }

    [Fact]
    public void Deliver_AnIdenticalMessageStillWaiting_AddsNothingAndReturnsTheWaitingOne()
    {
        var inbox = new AgentMessageInbox();
        var first = inbox.Deliver("pane-a", "pane-b", "question", "did you see this?");

        var second = inbox.Deliver("pane-a", "pane-b", "question", "did you see this?");

        Assert.Equal(AgentMessageDeliveryOutcome.Deduplicated, second.Outcome);
        Assert.Equal(first.Message!.Id, second.Message!.Id);
        Assert.Single(inbox.Drain("pane-b"));
    }

    /// <summary>
    /// Dedup is on the whole envelope, not on the body alone: the same sentence under a different label, or from a
    /// different sender, is a different message and must not be swallowed by the one already waiting.
    /// </summary>
    [Theory]
    [InlineData("pane-c", "question", "did you see this?")]
    [InlineData("pane-a", "heads-up", "did you see this?")]
    [InlineData("pane-a", "question", "did you see that?")]
    public void Deliver_AMessageDifferingInAnyField_IsNotTreatedAsADuplicate(string from, string kind, string body)
    {
        var inbox = new AgentMessageInbox();
        inbox.Deliver("pane-a", "pane-b", "question", "did you see this?");

        var second = inbox.Deliver(from, "pane-b", kind, body);

        Assert.Equal(AgentMessageDeliveryOutcome.Delivered, second.Outcome);
        Assert.Equal(2, inbox.Drain("pane-b").Count);
    }

    /// <summary>
    /// Only messages still waiting count as duplicates. Once the recipient has read and acted on something, the
    /// sender must be able to say it again — otherwise "ping me when you are done" could be said exactly once for
    /// the life of the app.
    /// </summary>
    [Fact]
    public void Deliver_TheSameMessageAgainAfterItWasRead_IsANewMessage()
    {
        var inbox = new AgentMessageInbox();
        inbox.Deliver("pane-a", "pane-b", "question", "are you done?");
        inbox.Drain("pane-b");

        var again = inbox.Deliver("pane-a", "pane-b", "question", "are you done?");

        Assert.Equal(AgentMessageDeliveryOutcome.Delivered, again.Outcome);
        Assert.Single(inbox.Drain("pane-b"));
    }

    [Fact]
    public void Drain_HandsEachMessageOverExactlyOnce_OldestFirst()
    {
        var inbox = new AgentMessageInbox();
        inbox.Deliver("pane-a", "pane-b", "note", "first");
        inbox.Deliver("pane-a", "pane-b", "note", "second");

        var drained = inbox.Drain("pane-b");

        Assert.Equal(new[] { "first", "second" }, drained.Select(message => message.Body).ToArray());
        Assert.Empty(inbox.Drain("pane-b"));
    }

    [Fact]
    public void Drain_ForAPaneWithNothingWaiting_IsEmpty()
    {
        var inbox = new AgentMessageInbox();

        Assert.Empty(inbox.Drain("pane-b"));
    }

    [Fact]
    public void Drain_OnlyTakesTheNamedPanesMessages()
    {
        var inbox = new AgentMessageInbox();
        inbox.Deliver("pane-a", "pane-b", "note", "for B");
        inbox.Deliver("pane-a", "pane-c", "note", "for C");

        Assert.Equal("for B", Assert.Single(inbox.Drain("pane-b")).Body);
        Assert.Equal("for C", Assert.Single(inbox.Drain("pane-c")).Body);
    }

    [Fact]
    public void Forget_DropsThePanesUnreadMessages()
    {
        var inbox = new AgentMessageInbox();
        inbox.Deliver("pane-a", "pane-b", "note", "never read");

        inbox.Forget("pane-b");

        Assert.Empty(inbox.Drain("pane-b"));
    }

    [Fact]
    public void Forget_LeavesEveryOtherPanesInboxAlone()
    {
        var inbox = new AgentMessageInbox();
        inbox.Deliver("pane-a", "pane-b", "note", "for B");
        inbox.Deliver("pane-a", "pane-c", "note", "for C");

        inbox.Forget("pane-b");

        Assert.Empty(inbox.Drain("pane-b"));
        Assert.Single(inbox.Drain("pane-c"));
    }

    /// <summary>
    /// A closing sender does not take its delivered messages with it: they belong to recipients who are still
    /// live and can still read them.
    /// </summary>
    [Fact]
    public void Forget_TheSender_LeavesWhatItAlreadySentWaitingForTheRecipient()
    {
        var inbox = new AgentMessageInbox();
        inbox.Deliver("pane-a", "pane-b", "handover", "the branch is pushed");

        inbox.Forget("pane-a");

        Assert.Single(inbox.Drain("pane-b"));
    }

    [Fact]
    public void Forget_APaneWithNoInbox_IsANoOp()
    {
        var inbox = new AgentMessageInbox();

        inbox.Forget("pane-b");

        Assert.Empty(inbox.Drain("pane-b"));
    }

    [Fact]
    public void Deliver_PastTheCap_IsRefusedWithoutEvictingWhatIsAlreadyWaiting()
    {
        var inbox = new AgentMessageInbox();
        for (var i = 0; i < AgentMessageInbox.MaxWaitingPerPane; i++)
        {
            inbox.Deliver("pane-a", "pane-b", "note", $"message {i}");
        }

        var refused = inbox.Deliver("pane-a", "pane-b", "note", "one too many");

        Assert.Equal(AgentMessageDeliveryOutcome.RecipientInboxFull, refused.Outcome);
        Assert.Null(refused.Message);
        var waiting = inbox.Drain("pane-b");
        Assert.Equal(AgentMessageInbox.MaxWaitingPerPane, waiting.Count);
        Assert.Equal("message 0", waiting[0].Body);
    }

    /// <summary>
    /// The store exists to be written from several sessions' MCP request threads at once, and a delivery is a
    /// check-then-act (is an identical message waiting? is it full?) that only holds under a lock. Many senders
    /// aiming the same message at one recipient must leave exactly one copy — a concurrent-dictionary-shaped
    /// implementation would let two threads both see "no duplicate" and both add one.
    /// </summary>
    [Fact]
    public async Task ConcurrentDeliveriesOfTheSameMessage_LeaveExactlyOneWaiting()
    {
        var inbox = new AgentMessageInbox();

        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
            inbox.Deliver("pane-a", "pane-b", "question", "did you see this?"))));

        Assert.Single(inbox.Drain("pane-b"));
    }

    [Fact]
    public async Task ConcurrentDeliveriesDrainsAndForgets_AcrossManyPanes_NeverThrowAndLoseNothing()
    {
        var inbox = new AgentMessageInbox();
        const int paneCount = 64;

        // Every pane gets its own distinct message, and half of them are drained or forgotten while the rest are
        // still being written — all of it racing on one store.
        var work = Enumerable.Range(0, paneCount).Select(index => Task.Run(() =>
        {
            var paneId = $"pane-{index}";
            inbox.Deliver("sender", paneId, "note", $"message {index}");
            if (index % 2 == 0)
            {
                inbox.Forget(paneId);
            }
        }));

        await Task.WhenAll(work);

        for (var index = 0; index < paneCount; index++)
        {
            var waiting = inbox.Drain($"pane-{index}");
            Assert.Equal(index % 2 == 0 ? 0 : 1, waiting.Count);
        }
    }
}
