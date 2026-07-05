using Cockpit.Core.Notifications;
using FluentAssertions;

namespace Cockpit.Core.Tests.Notifications;

/// <summary>
/// The pure idle/lock → present/away kernel, tested without any OS P/Invoke: the Windows detector
/// only measures idle + lock and delegates the rule here.
/// </summary>
public class PresenceDecisionTests
{
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(15);

    [Fact]
    public void Decide_RecentInput_Unlocked_IsPresent()
    {
        PresenceDecision.Decide(TimeSpan.FromMinutes(2), isLocked: false, Threshold)
            .Should().Be(PresenceState.Present);
    }

    [Fact]
    public void Decide_IdlePastThreshold_Unlocked_IsAway()
    {
        PresenceDecision.Decide(TimeSpan.FromMinutes(20), isLocked: false, Threshold)
            .Should().Be(PresenceState.Away);
    }

    [Fact]
    public void Decide_IdleExactlyAtThreshold_IsAway()
    {
        // >= threshold counts as away, so the boundary itself is away.
        PresenceDecision.Decide(Threshold, isLocked: false, Threshold)
            .Should().Be(PresenceState.Away);
    }

    [Fact]
    public void Decide_Locked_IsAway_EvenWithRecentInput()
    {
        PresenceDecision.Decide(TimeSpan.Zero, isLocked: true, Threshold)
            .Should().Be(PresenceState.Away);
    }
}
