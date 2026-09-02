using Cockpit.Core.Notifications;

namespace Cockpit.Core.Tests.Notifications;

/// <summary>
/// The pure idle/lock → present/away kernel, tested without any OS P/Invoke: the Windows detector
/// only measures idle + lock and delegates the rule here.
/// </summary>
public class PresenceDecisionTests
{
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(15);

    // Idle at or past the threshold counts as away, so the boundary itself is away; and a locked screen is away
    // however recent the input was.
    [Theory]
    [InlineData(2, false, PresenceState.Present)]
    [InlineData(20, false, PresenceState.Away)]
    [InlineData(15, false, PresenceState.Away)]
    [InlineData(0, true, PresenceState.Away)]
    public void Decide_ReadsIdleTimeAndTheLockScreen(int idleMinutes, bool isLocked, PresenceState expected)
    {
        Assert.Equal(expected, PresenceDecision.Decide(TimeSpan.FromMinutes(idleMinutes), isLocked, Threshold));
    }
}
