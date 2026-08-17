using Cockpit.Core.Diagnostics;

namespace Cockpit.Core.Tests.Diagnostics;

/// <summary>
/// Tests <see cref="RenderClockHeartbeat"/> — the same hysteresis shape as <see cref="UiThreadHeartbeatTests"/>,
/// measured against how long a forced compositor commit has gone unprocessed instead of a dispatcher pong.
/// </summary>
public class RenderClockHeartbeatTests
{
    private static readonly TimeSpan Stall = RenderClockHeartbeat.StallAfter;

    [Fact]
    public void NoProbeOutstanding_SaysNothing() =>
        Assert.Equal(
            new RenderClockHeartbeatDecision(false, false, false),
            RenderClockHeartbeat.Decide(probeInFlightFor: null, warned: false));

    [Fact]
    public void AProbeStillWellWithinItsBudget_SaysNothing() =>
        Assert.False(RenderClockHeartbeat.Decide(TimeSpan.FromSeconds(1), warned: false).Stalled);

    [Fact]
    public void RightAtTheThreshold_NotYetOverIt_SaysNothing() =>
        Assert.False(RenderClockHeartbeat.Decide(Stall, warned: false).Stalled);

    [Fact]
    public void AProbeOutstandingPastTheThreshold_ReportsAStall()
    {
        var decision = RenderClockHeartbeat.Decide(Stall + TimeSpan.FromSeconds(1), warned: false);

        Assert.True(decision.Stalled);
        Assert.False(decision.Resumed);
        Assert.True(decision.Warned, "so the next tick does not say it again");
    }

    [Fact]
    public void HavingReportedOnce_ItDoesNotRepeatEveryTick()
    {
        var decision = RenderClockHeartbeat.Decide(Stall + TimeSpan.FromMinutes(5), warned: true);

        Assert.False(decision.Stalled, "a warning every second is a warning you turn off");
        Assert.False(decision.Resumed);
        Assert.True(decision.Warned);
    }

    [Fact]
    public void TheProbeComingBack_ReportsRecovery()
    {
        var decision = RenderClockHeartbeat.Decide(probeInFlightFor: null, warned: true);

        Assert.False(decision.Stalled);
        Assert.True(decision.Resumed);
        Assert.False(decision.Warned, "let off the hook, so a later stall is heard again");
    }

    [Fact]
    public void AfterRecovering_ALaterStallReportsAgain()
    {
        var recovered = RenderClockHeartbeat.Decide(probeInFlightFor: null, warned: true);

        Assert.True(RenderClockHeartbeat.Decide(Stall + TimeSpan.FromSeconds(1), recovered.Warned).Stalled);
    }

    [Fact]
    public void APostedProbeTheUiThreadHasNotStartedYet_IsNotAStall() =>
        // The caller reports null until the commit is actually requested on the UI thread, so a hung dispatcher
        // is left to the UI-thread heartbeat instead of being blamed on the render clock.
        Assert.False(RenderClockHeartbeat.Decide(probeInFlightFor: null, warned: false).Stalled);
}
