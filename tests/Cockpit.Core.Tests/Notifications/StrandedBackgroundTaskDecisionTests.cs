using Cockpit.Core.Notifications;

namespace Cockpit.Core.Tests.Notifications;

/// <summary>
/// The safety net under a session whose background shell finished without the provider saying so (AC-1273). What
/// these pin down is mostly when it stays quiet: a delivery that worked, a turn already running, a session waiting
/// on the operator, or a grace that has not run out all leave it alone. A net that produced turns of its own would
/// be worse than the gap it covers, so "does nothing" is the behaviour under test.
/// </summary>
public class StrandedBackgroundTaskDecisionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

    // The one case that fires: the shell ended, the session's turn is over, and nothing has happened since.
    [Fact]
    public void IsStranded_WhenAFinishedSessionHasDoneNothingSinceItsShellEnded()
    {
        var endedAt = Now - Grace;

        Assert.True(StrandedBackgroundTaskDecision.IsStranded(
            isFinished: true, endedAt, lastActivity: endedAt, Now, Grace));
    }

    // The provider doing its own job is the normal case and must never reach the net. Its notification opens a turn,
    // which stamps the session's activity later than the moment the shell left the task list — measured at under a
    // second, so a two-minute grace is not what keeps this quiet: this is.
    [Fact]
    public void ADeliveryThatWorked_LeavesTheSessionAlone()
    {
        var endedAt = Now - Grace;

        Assert.False(StrandedBackgroundTaskDecision.IsStranded(
            isFinished: true, endedAt, lastActivity: endedAt.AddSeconds(1), Now, Grace));
    }

    // A turn that is already running, or one waiting on an answer from the operator, is not stranded — sending into
    // it is exactly the second turn this must never produce. The caller passes the same wakeable set an urgent
    // notify from a peer is allowed to interrupt.
    [Fact]
    public void ASessionThatIsNotFinished_IsNeverStranded()
    {
        var endedAt = Now - TimeSpan.FromHours(1);

        Assert.False(StrandedBackgroundTaskDecision.IsStranded(
            isFinished: false, endedAt, lastActivity: endedAt, Now, Grace));
    }

    // Inside the grace nothing happens yet, and a zero grace turns the net off altogether.
    [Theory]
    [InlineData(119, 2, false)]
    [InlineData(120, 2, true)]
    [InlineData(3600, 0, false)]
    public void TheGraceHasToRunOutFirst(int quietSeconds, int graceMinutes, bool expected)
    {
        var endedAt = Now.AddSeconds(-quietSeconds);

        Assert.Equal(
            expected,
            StrandedBackgroundTaskDecision.IsStranded(
                isFinished: true, endedAt, lastActivity: endedAt, Now, TimeSpan.FromMinutes(graceMinutes)));
    }
}
