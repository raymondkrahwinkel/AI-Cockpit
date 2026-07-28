using Cockpit.Core.Abstractions.Agents;
using Cockpit.Infrastructure.Agents;

namespace Cockpit.Infrastructure.Tests.Agents;

/// <summary>
/// The inbox store on its own (AC-392), independent of the MCP tools that drive it: it is keyed on the recipient's
/// pane id alone, dedups an identical message that is still waiting, hands each message over exactly once and in
/// bounded batches, can take a single message back out again, and gives a closing session's unread mail back rather
/// than holding it for the life of the app. It deliberately does
/// <em>not</em> know about workspaces — the boundary is enforced at notify time by <c>AgentsMcpTools</c> against
/// the gateway's snapshot, so there is nothing to prove about isolation here.
/// </summary>
public sealed class AgentMessageInboxTests
{
    /// <summary>
    /// Drains with a limit no test here reaches, so a test about dedup, handover or Forget is not quietly also a test
    /// about batching — that has its own tests, which are the only ones that pass a limit small enough to bite.
    /// </summary>
    private static IReadOnlyList<AgentMessage> _DrainAll(AgentMessageInbox inbox, string paneId) =>
        inbox.Drain(paneId, int.MaxValue).Messages;

    [Fact]
    public void Deliver_ThenDrain_HandsBackTheWholeEnvelope()
    {
        var inbox = new AgentMessageInbox();

        var delivery = inbox.Deliver("pane-a", "pane-b", "question", "who owns the parser?");

        Assert.Equal(AgentMessageDeliveryOutcome.Delivered, delivery.Outcome);
        var waiting = Assert.Single(_DrainAll(inbox, "pane-b"));
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
        Assert.Single(_DrainAll(inbox, "pane-b"));
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
        Assert.Equal(2, _DrainAll(inbox, "pane-b").Count);
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
        _DrainAll(inbox, "pane-b");

        var again = inbox.Deliver("pane-a", "pane-b", "question", "are you done?");

        Assert.Equal(AgentMessageDeliveryOutcome.Delivered, again.Outcome);
        Assert.Single(_DrainAll(inbox, "pane-b"));
    }

    [Fact]
    public void Drain_HandsEachMessageOverExactlyOnce_OldestFirst()
    {
        var inbox = new AgentMessageInbox();
        inbox.Deliver("pane-a", "pane-b", "note", "first");
        inbox.Deliver("pane-a", "pane-b", "note", "second");

        var drained = _DrainAll(inbox, "pane-b");

        Assert.Equal(new[] { "first", "second" }, drained.Select(message => message.Body).ToArray());
        Assert.Empty(_DrainAll(inbox, "pane-b"));
    }

    [Fact]
    public void Drain_ForAPaneWithNothingWaiting_IsEmpty()
    {
        var inbox = new AgentMessageInbox();

        Assert.Empty(_DrainAll(inbox, "pane-b"));
    }

    [Fact]
    public void Drain_OnlyTakesTheNamedPanesMessages()
    {
        var inbox = new AgentMessageInbox();
        inbox.Deliver("pane-a", "pane-b", "note", "for B");
        inbox.Deliver("pane-a", "pane-c", "note", "for C");

        Assert.Equal("for B", Assert.Single(_DrainAll(inbox, "pane-b")).Body);
        Assert.Equal("for C", Assert.Single(_DrainAll(inbox, "pane-c")).Body);
    }

    [Fact]
    public void Forget_DropsThePanesUnreadMessages()
    {
        var inbox = new AgentMessageInbox();
        inbox.Deliver("pane-a", "pane-b", "note", "never read");

        inbox.Forget("pane-b");

        Assert.Empty(_DrainAll(inbox, "pane-b"));
    }

    [Fact]
    public void Forget_LeavesEveryOtherPanesInboxAlone()
    {
        var inbox = new AgentMessageInbox();
        inbox.Deliver("pane-a", "pane-b", "note", "for B");
        inbox.Deliver("pane-a", "pane-c", "note", "for C");

        inbox.Forget("pane-b");

        Assert.Empty(_DrainAll(inbox, "pane-b"));
        Assert.Single(_DrainAll(inbox, "pane-c"));
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

        Assert.Single(_DrainAll(inbox, "pane-b"));
    }

    [Fact]
    public void Forget_APaneWithNoInbox_IsANoOp()
    {
        var inbox = new AgentMessageInbox();

        inbox.Forget("pane-b");

        Assert.Empty(_DrainAll(inbox, "pane-b"));
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
        var waiting = _DrainAll(inbox, "pane-b");
        Assert.Equal(AgentMessageInbox.MaxWaitingPerPane, waiting.Count);
        Assert.Equal("message 0", waiting[0].Body);
    }

    /// <summary>
    /// The drain is bounded because the batch becomes one tool result in the recipient's context: a neighbour must not
    /// be able to decide how much of that context the recipient spends. The rest stays put and is reported, so the tail
    /// is collectable rather than lost.
    /// </summary>
    [Fact]
    public void Drain_PastTheLimit_HandsBackTheOldestAndReportsTheRest()
    {
        var inbox = new AgentMessageInbox();
        for (var i = 0; i < 5; i++)
        {
            inbox.Deliver("pane-a", "pane-b", "note", $"message {i}");
        }

        var batch = inbox.Drain("pane-b", 2);

        Assert.Equal(new[] { "message 0", "message 1" }, batch.Messages.Select(message => message.Body).ToArray());
        Assert.Equal(3, batch.Remaining);
    }

    /// <summary>
    /// A capped drain is a handover of that batch and nothing more: what it left behind must still be there, in order,
    /// for the next call — and the last call must report nothing remaining rather than leaving the recipient guessing.
    /// </summary>
    [Fact]
    public void Drain_Repeatedly_CollectsTheWholeInboxInOrderWithoutRepeatingAMessage()
    {
        var inbox = new AgentMessageInbox();
        for (var i = 0; i < 5; i++)
        {
            inbox.Deliver("pane-a", "pane-b", "note", $"message {i}");
        }

        var first = inbox.Drain("pane-b", 2);
        var second = inbox.Drain("pane-b", 2);
        var third = inbox.Drain("pane-b", 2);

        Assert.Equal(
            new[] { "message 0", "message 1", "message 2", "message 3", "message 4" },
            first.Messages.Concat(second.Messages).Concat(third.Messages).Select(message => message.Body).ToArray());
        Assert.Equal(0, third.Remaining);
        Assert.Empty(inbox.Drain("pane-b", 2).Messages);
    }

    /// <summary>
    /// A drain that hands over nothing must not read as an empty inbox: the caller is told what is still waiting, or a
    /// bad limit silently loses mail that is in fact still there.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Drain_WithANonPositiveLimit_TakesNothingAndReportsEverythingAsWaiting(int limit)
    {
        var inbox = new AgentMessageInbox();
        inbox.Deliver("pane-a", "pane-b", "note", "still here");

        var batch = inbox.Drain("pane-b", limit);

        Assert.Empty(batch.Messages);
        Assert.Equal(1, batch.Remaining);
        Assert.Single(_DrainAll(inbox, "pane-b"));
    }

    /// <summary>
    /// The window this closes: the notify tool checks the recipient is on the caller's desk, then delivers, and the
    /// recipient's session can end in between — its Forget running before the delivery lands. Without a way to take one
    /// message back out, that message waits under a pane id no session answers to for the life of the app.
    /// </summary>
    [Fact]
    public void Retract_RemovesOnlyTheNamedMessage_AndReportsThatItWasThere()
    {
        var inbox = new AgentMessageInbox();
        var first = inbox.Deliver("pane-a", "pane-b", "note", "keep me");
        var second = inbox.Deliver("pane-a", "pane-b", "note", "take me back");

        Assert.True(inbox.Retract("pane-b", second.Message!.Id));

        var waiting = Assert.Single(_DrainAll(inbox, "pane-b"));
        Assert.Equal(first.Message!.Id, waiting.Id);
    }

    /// <summary>
    /// Retracting the only message must leave no inbox behind under that pane id — an empty list held for a pane that
    /// has just closed is the residue the retraction exists to prevent, only smaller.
    /// </summary>
    [Fact]
    public void Retract_TheLastMessage_LeavesNoInboxBehind()
    {
        var inbox = new AgentMessageInbox();
        var delivered = inbox.Deliver("pane-a", "pane-b", "note", "only one");

        Assert.True(inbox.Retract("pane-b", delivered.Message!.Id));

        // The next identical send is a fresh delivery, not a dedup onto something still waiting — which is only true if
        // the retracted message really is gone rather than merely hidden.
        Assert.Equal(
            AgentMessageDeliveryOutcome.Delivered,
            inbox.Deliver("pane-a", "pane-b", "note", "only one").Outcome);
    }

    /// <summary>
    /// A retraction that finds nothing says so rather than pretending: the message was already drained (the recipient
    /// read it before the sender thought better of it), or already retracted.
    /// </summary>
    [Fact]
    public void Retract_AMessageThatIsNoLongerWaiting_IsFalseAndTouchesNothing()
    {
        var inbox = new AgentMessageInbox();
        var delivered = inbox.Deliver("pane-a", "pane-b", "note", "already read");
        _DrainAll(inbox, "pane-b");

        Assert.False(inbox.Retract("pane-b", delivered.Message!.Id));
        Assert.False(inbox.Retract("pane-c", delivered.Message!.Id));
        Assert.False(inbox.Retract("pane-b", "not-a-message-id"));
    }

    /// <summary>
    /// Retract is narrower than Forget on purpose: the recipient's other mail was delivered by other senders on their
    /// own merits, and one sender taking its own message back must not drop theirs with it.
    /// </summary>
    [Fact]
    public void Retract_LeavesTheRecipientsMailFromOtherSendersAlone()
    {
        var inbox = new AgentMessageInbox();
        var mine = inbox.Deliver("pane-a", "pane-b", "note", "mine");
        inbox.Deliver("pane-c", "pane-b", "note", "someone else's");

        inbox.Retract("pane-b", mine.Message!.Id);

        Assert.Equal("someone else's", Assert.Single(_DrainAll(inbox, "pane-b")).Body);
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

        Assert.Single(_DrainAll(inbox, "pane-b"));
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
            var waiting = _DrainAll(inbox, $"pane-{index}");
            Assert.Equal(index % 2 == 0 ? 0 : 1, waiting.Count);
        }
    }
}
