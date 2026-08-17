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

    // AC-883: the destructive half. Collapsing a transcript on a machine that is merely loaded blanks a pane the
    // operator is reading and loses their scroll position, so these pin the gap between "worth logging" and
    // "worth acting on" — and that Windows and X11 never reach the second one at all.
    private static readonly TimeSpan Pause = RenderClockHeartbeat.PauseAfter;

    [Fact]
    public void ThePauseThresholdIsFarAboveTheOneThatMerelyLogs() =>
        Assert.True(Pause >= RenderClockHeartbeat.StallAfter * 4);

    [Theory]
    // A healthy round trip, a loaded one, a GPU-bound one, a commit queued behind a long streaming burst, and one
    // that took a hundred times longer than any of those. All of them come back; a stopped clock never does.
    [InlineData(2)]
    [InlineData(80)]
    [InlineData(400)]
    [InlineData(3_000)]
    [InlineData(14_000)]
    public void ABusyButLiveMachine_IsNeverReadAsPaused(int roundTripMs) =>
        Assert.False(
            RenderClockHeartbeat.ShouldPauseRenderers(TimeSpan.FromMilliseconds(roundTripMs), isMacOs: true),
            "load makes a commit slower, not absent — only a clock that stopped delivering ticks may pause a pane");

    [Fact]
    public void AProbeStallingLongEnoughToLog_DoesNotYetPause() =>
        // The whole point of two thresholds: the log line goes out at 15s so a reproduction is captured, while the
        // pane keeps rendering. Nothing the operator can see changes on the strength of that first signal.
        Assert.False(RenderClockHeartbeat.ShouldPauseRenderers(Stall + TimeSpan.FromSeconds(1), isMacOs: true));

    [Fact]
    public void AHungUiThreadThatHasNotStartedTheProbe_DoesNotPause() =>
        // The caller reports null until the commit is actually requested on the UI thread. A frozen dispatcher is
        // the UI-thread heartbeat's business, and must never blank a transcript on the render clock's account.
        Assert.False(RenderClockHeartbeat.ShouldPauseRenderers(probeInFlightFor: null, isMacOs: true));

    [Fact]
    public void OneCommitUnprocessedForOverAMinuteOnMacOs_Pauses() =>
        Assert.True(RenderClockHeartbeat.ShouldPauseRenderers(Pause + TimeSpan.FromSeconds(1), isMacOs: true));

    [Fact]
    public void RightAtThePauseThreshold_NotYetOverIt_DoesNotPause() =>
        Assert.False(RenderClockHeartbeat.ShouldPauseRenderers(Pause, isMacOs: true));

    [Theory]
    [InlineData(61)]
    [InlineData(600)]
    [InlineData(86_400)]
    public void OnWindowsAndLinux_NoStallHoweverLongEverPauses(int stalledForSeconds) =>
        // Their render clock is a software sleep loop the OS cannot take away, and Minimized is full coverage of
        // pausing there. Their working behaviour after af2fe273/cc85ca1e stays exactly as it was.
        Assert.False(
            RenderClockHeartbeat.ShouldPauseRenderers(TimeSpan.FromSeconds(stalledForSeconds), isMacOs: false));

    [Fact]
    public void APostedProbeTheUiThreadHasNotStartedYet_IsNotAStall() =>
        // The caller reports null until the commit is actually requested on the UI thread, so a hung dispatcher
        // is left to the UI-thread heartbeat instead of being blamed on the render clock.
        Assert.False(RenderClockHeartbeat.Decide(probeInFlightFor: null, warned: false).Stalled);
}
