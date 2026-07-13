using Cockpit.Core.Notifications;
using FluentAssertions;

namespace Cockpit.Core.Tests.Notifications;

/// <summary>
/// When a finished session falls quiet: only a session that is actually done drops back to idle, and only once
/// it has been quiet for the whole threshold. A session that is busy or waiting on you is never idle, however
/// long it sits there — the waiting is the work.
/// </summary>
public class SessionIdleDecisionTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Finished_AndQuietPastTheThreshold_BecomesIdle()
    {
        SessionIdleDecision.BecomesIdle(isFinished: true, Now.AddMinutes(-6), Now, TimeSpan.FromMinutes(5))
            .Should().BeTrue();
    }

    [Fact]
    public void Finished_ButQuietForLessThanTheThreshold_StaysDone()
    {
        SessionIdleDecision.BecomesIdle(isFinished: true, Now.AddMinutes(-4), Now, TimeSpan.FromMinutes(5))
            .Should().BeFalse();
    }

    [Fact]
    public void NotFinished_NeverBecomesIdle_HoweverLongItHasBeenQuiet()
    {
        SessionIdleDecision.BecomesIdle(isFinished: false, Now.AddHours(-3), Now, TimeSpan.FromMinutes(5))
            .Should().BeFalse();
    }

    [Fact]
    public void ZeroThreshold_TurnsTheRuleOff()
    {
        SessionIdleDecision.BecomesIdle(isFinished: true, Now.AddHours(-3), Now, TimeSpan.Zero)
            .Should().BeFalse();
    }
}
