using Cockpit.Core.Abstractions.Agents;
using Cockpit.Infrastructure.Agents;

namespace Cockpit.Infrastructure.Tests.Agents;

/// <summary>
/// The rate limit itself (AC-396), independent of the tools that charge it. Every test states its own limits and
/// window rather than leaning on the shipped defaults: what has to hold is the shape of the guard rail — a sliding
/// window, per sender, per activity — and a test that had to send twenty messages to reach the interesting case
/// would be asserting the constant instead.
/// <para>
/// Time is moved rather than waited out, so the window's edges are exact. That is also what makes the two
/// mutation-sensitive spots reachable: the expiry comparison (is an attempt exactly one window old still counted)
/// and the clamp on <see cref="AgentLineBudgetVerdict.RetryAfter"/> when the clock steps backwards.
/// </para>
/// </summary>
public sealed class AgentLineBudgetTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>A clock that only moves when a test moves it — the whole point being that a window edge is a fact, not a race.</summary>
    private sealed class StoppedClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;

        public void StepBack(TimeSpan by) => _now -= by;
    }

    private static (AgentLineBudget Budget, StoppedClock Clock) _Budget(int messages = 3, int wakes = 2)
    {
        var clock = new StoppedClock(Start);
        return (new AgentLineBudget(clock, Window, messages, wakes), clock);
    }

    [Fact]
    public void Charge_UnderTheLimit_IsAllowedAndSaysWhatHasBeenSpent()
    {
        var (budget, _) = _Budget(messages: 3);

        var first = budget.Charge("pane-a", AgentLineActivity.Message);
        var second = budget.Charge("pane-a", AgentLineActivity.Message);

        Assert.True(first.Allowed);
        Assert.Equal(1, first.Used);
        Assert.True(second.Allowed);
        Assert.Equal(2, second.Used);
        Assert.Equal(3, second.Limit);
        Assert.Equal(Window, second.Window);
        Assert.Equal(TimeSpan.Zero, second.RetryAfter);
    }

    /// <summary>The cap fires, and the sender is told how long to hold off rather than only that it may not.</summary>
    [Fact]
    public void Charge_AtTheLimit_IsRefusedAndSaysHowLongUntilThereIsRoom()
    {
        var (budget, clock) = _Budget(messages: 2);

        budget.Charge("pane-a", AgentLineActivity.Message);
        clock.Advance(TimeSpan.FromSeconds(20));
        budget.Charge("pane-a", AgentLineActivity.Message);
        clock.Advance(TimeSpan.FromSeconds(10));

        var refused = budget.Charge("pane-a", AgentLineActivity.Message);

        Assert.False(refused.Allowed);
        Assert.Equal(2, refused.Used);
        // The oldest attempt was 30 seconds ago and the window is a minute, so room appears in 30 — measured from the
        // oldest counted attempt, not from now, which is the difference between a sliding window and a cooldown.
        Assert.Equal(TimeSpan.FromSeconds(30), refused.RetryAfter);
    }

    /// <summary>
    /// The refusal does not itself count. Without this a sender that keeps trying keeps pushing its own window
    /// forward, and a guard rail meant to lift within the minute becomes a lockout for as long as the agent is
    /// looping — which is precisely the agent that will loop.
    /// </summary>
    [Fact]
    public void Charge_Refused_IsNotItselfCounted()
    {
        var (budget, clock) = _Budget(messages: 1);

        budget.Charge("pane-a", AgentLineActivity.Message);

        clock.Advance(TimeSpan.FromSeconds(30));
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.False(budget.Charge("pane-a", AgentLineActivity.Message).Allowed);
        }

        // A full window after the one attempt that was allowed — not after the last refusal, which is 30 seconds
        // later and would still be inside the window if refusals counted.
        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.True(budget.Charge("pane-a", AgentLineActivity.Message).Allowed);
    }

    /// <summary>An attempt exactly one window old is out, not still in — the boundary the expiry comparison decides.</summary>
    [Fact]
    public void Charge_AnAttemptExactlyOneWindowOld_NoLongerCounts()
    {
        var (budget, clock) = _Budget(messages: 1);

        budget.Charge("pane-a", AgentLineActivity.Message);

        clock.Advance(Window - TimeSpan.FromTicks(1));
        Assert.False(budget.Charge("pane-a", AgentLineActivity.Message).Allowed);

        clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(budget.Charge("pane-a", AgentLineActivity.Message).Allowed);
    }

    /// <summary>
    /// A message and a wake are separate allowances. One counter for both would have to be either loose enough to let
    /// a wake loop through or tight enough to stop ordinary talking.
    /// </summary>
    [Fact]
    public void Charge_MessagesAndWakes_AreCountedApart()
    {
        var (budget, _) = _Budget(messages: 3, wakes: 1);

        Assert.True(budget.Charge("pane-a", AgentLineActivity.Wake).Allowed);
        Assert.False(budget.Charge("pane-a", AgentLineActivity.Wake).Allowed);

        // The wake allowance is spent; the message allowance is untouched.
        var message = budget.Charge("pane-a", AgentLineActivity.Message);
        Assert.True(message.Allowed);
        Assert.Equal(1, message.Used);
        Assert.Equal(3, message.Limit);
    }

    /// <summary>
    /// AC-119 scenario S10, at the store: one sender at its cap does not stop another. This is the half a limit that
    /// counted arrivals rather than sends would get wrong — there, one loud neighbour fills a recipient's inbox and
    /// every innocent sender is refused for something it did not do.
    /// </summary>
    [Fact]
    public void Charge_OnePaneAtItsLimit_LeavesEveryOtherPaneUntouched()
    {
        var (budget, _) = _Budget(messages: 1);

        Assert.True(budget.Charge("loud", AgentLineActivity.Message).Allowed);
        Assert.False(budget.Charge("loud", AgentLineActivity.Message).Allowed);

        Assert.True(budget.Charge("quiet", AgentLineActivity.Message).Allowed);
        Assert.True(budget.Charge("also-quiet", AgentLineActivity.Wake).Allowed);
    }

    /// <summary>
    /// A clock the OS steps backwards must not hand an agent a negative wait: the number exists to tell it how long
    /// to hold off, and a negative one reads as nonsense exactly where it is meant to be read.
    /// </summary>
    [Fact]
    public void Charge_WhenTheClockStepsBackwards_NeverReportsANegativeWait()
    {
        var (budget, clock) = _Budget(messages: 1);

        budget.Charge("pane-a", AgentLineActivity.Message);
        clock.StepBack(TimeSpan.FromMinutes(10));

        var refused = budget.Charge("pane-a", AgentLineActivity.Message);

        Assert.False(refused.Allowed);
        Assert.True(refused.RetryAfter >= TimeSpan.Zero, $"RetryAfter was {refused.RetryAfter}");
    }

    /// <summary>What the operator's read shows — and does not show, once a window has passed.</summary>
    [Fact]
    public void Usage_ReportsWhatIsStillInsideTheWindowAndNothingElse()
    {
        var (budget, clock) = _Budget(messages: 3, wakes: 2);

        budget.Charge("pane-a", AgentLineActivity.Message);
        budget.Charge("pane-a", AgentLineActivity.Message);
        budget.Charge("pane-a", AgentLineActivity.Wake);
        budget.Charge("pane-b", AgentLineActivity.Message);

        var usage = budget.Usage();

        Assert.Equal(2, usage.Single(u => u.PaneId == "pane-a" && u.Activity == AgentLineActivity.Message).Used);
        Assert.Equal(1, usage.Single(u => u.PaneId == "pane-a" && u.Activity == AgentLineActivity.Wake).Used);
        Assert.Equal(1, usage.Single(u => u.PaneId == "pane-b" && u.Activity == AgentLineActivity.Message).Used);
        Assert.Equal(2, usage.Single(u => u.Activity == AgentLineActivity.Wake).Limit);

        clock.Advance(Window);

        Assert.Empty(budget.Usage());
    }

    [Fact]
    public void Forget_DropsWhatAPaneSpentAndLeavesItsNeighboursAlone()
    {
        var (budget, _) = _Budget(messages: 1, wakes: 1);

        budget.Charge("pane-a", AgentLineActivity.Message);
        budget.Charge("pane-a", AgentLineActivity.Wake);
        budget.Charge("pane-b", AgentLineActivity.Message);

        budget.Forget("pane-a");

        // Both of pane-a's counters, not only the one that happens to be checked first.
        Assert.True(budget.Charge("pane-a", AgentLineActivity.Message).Allowed);
        Assert.True(budget.Charge("pane-a", AgentLineActivity.Wake).Allowed);
        Assert.False(budget.Charge("pane-b", AgentLineActivity.Message).Allowed);
    }

    [Fact]
    public void Forget_OfAPaneThatSpentNothing_IsANoOp()
    {
        var (budget, _) = _Budget();

        budget.Forget("never-seen");

        Assert.Empty(budget.Usage());
    }

    /// <summary>
    /// The shipped defaults, asserted as a relationship rather than as two numbers: a wake costs the recipient's
    /// operator a turn where a message only waits, so the wake allowance has to be the smaller of the two. A later
    /// edit that raises one without the other should fail here rather than quietly make waking as cheap as talking.
    /// </summary>
    [Fact]
    public void Defaults_AllowFewerWakesThanMessages()
    {
        Assert.True(
            AgentLineBudget.MaxWakesPerWindow < AgentLineBudget.MaxMessagesPerWindow,
            $"wakes ({AgentLineBudget.MaxWakesPerWindow}) must stay scarcer than messages ({AgentLineBudget.MaxMessagesPerWindow})");
        Assert.True(AgentLineBudget.DefaultWindow > TimeSpan.Zero);
    }
}
