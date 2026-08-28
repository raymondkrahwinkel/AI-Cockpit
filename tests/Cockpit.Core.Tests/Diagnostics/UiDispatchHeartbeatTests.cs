using Cockpit.Core.Diagnostics;

namespace Cockpit.Core.Tests.Diagnostics;

/// <summary>
/// AC-1196: the rule that tells a starved dispatcher from a blocked one, and decides which of the two the render
/// clock may be blamed for. The end-to-end halves are Ac1196FreezeDetectorTests; the boundaries are here.
/// </summary>
public class UiDispatchHeartbeatTests
{
    private static readonly TimeSpan Starved = UiDispatchHeartbeat.StarvedAfter;
    private static readonly TimeSpan Over = Starved + TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Fresh = TimeSpan.FromMilliseconds(800);

    [Fact]
    public void APendingProbeOnAThreadStillAnsweringAboveIt_IsStarvation() =>
        Assert.True(UiDispatchHeartbeat.IsStarved(Over, Fresh));

    [Fact]
    public void RightAtTheThreshold_NotYetOverIt_IsNotYetStarvation() =>
        Assert.False(UiDispatchHeartbeat.IsStarved(Starved, Fresh));

    [Fact]
    public void APendingProbeOnAThreadThatAnswersNothing_IsNotStarvation() =>
        // The pong is posted every tick above a layout loop's priority, so a stale one means the thread is not
        // running at all. That is cause 1, and it is the render clock's to report — not this alarm's.
        Assert.False(UiDispatchHeartbeat.IsStarved(Over, sinceHighPriorityPong: Over));

    [Fact]
    public void APendingProbeOnAThreadThatHasNeverAnswered_IsNotStarvation() =>
        Assert.False(UiDispatchHeartbeat.IsStarved(Over, sinceHighPriorityPong: null));

    [Fact]
    public void APongOlderThanTheThreadCountsAsPumping_IsNotStarvation() =>
        Assert.False(UiDispatchHeartbeat.IsStarved(Over, UiDispatchHeartbeat.PumpingWithin + TimeSpan.FromSeconds(1)));

    [Fact]
    public void NothingPending_IsNeverStarvation() =>
        Assert.False(UiDispatchHeartbeat.IsStarved(pendingFor: null, sinceHighPriorityPong: Fresh));

    // T3: the misdiagnosis this ticket exists to stop. Under starvation the clock is ticking and the app draws,
    // so nothing may be handed to the render clock's decision — it would report a stall that is not happening.
    [Fact]
    public void UnderStarvation_TheRenderClockIsHandedNothingToReport()
    {
        var outstanding = UiDispatchHeartbeat.RenderClockOutstandingFor(startedFor: null, Over, Fresh);

        Assert.Null(outstanding);
        Assert.False(RenderClockHeartbeat.Decide(outstanding, warned: false).Stalled);
    }

    // T1: a thread that answers nothing cannot request the commit either, so the probe's whole wait is the render
    // clock's to answer for. This is the value that was null before this ticket, which is why four freezes read clean.
    [Fact]
    public void OnABlockedThread_TheWholePendingWaitGoesToTheRenderClock()
    {
        var outstanding = UiDispatchHeartbeat.RenderClockOutstandingFor(startedFor: null, Over, sinceHighPriorityPong: null);

        Assert.Equal(Over, outstanding);
        Assert.True(RenderClockHeartbeat.Decide(outstanding, warned: false).Stalled);
    }

    // T6: the path that already worked. A probe the UI thread started and the clock never answered stays the render
    // clock's, whatever the dispatcher is doing — a state was added here, not swapped for the one that works.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AProbeTheUiThreadAlreadyStarted_StaysTheRenderClocks(bool threadPumping)
    {
        var pong = threadPumping ? Fresh : Over;

        Assert.False(UiDispatchHeartbeat.IsStarved(pendingFor: null, pong));
        Assert.Equal(Over, UiDispatchHeartbeat.RenderClockOutstandingFor(Over, pendingFor: null, pong));
        Assert.True(RenderClockHeartbeat.Decide(Over, warned: false).Stalled);
    }

    // T7: the same call on a healthy thread, which is what makes the six above mean anything.
    [Fact]
    public void AQuietThreadWithNoProbeOutstanding_SaysNothingAtAll()
    {
        var decision = UiDispatchHeartbeat.Decide(pendingFor: null, Fresh, warned: false);

        Assert.Equal(new UiDispatchDecision(false, false, false), decision);
        Assert.Null(UiDispatchHeartbeat.RenderClockOutstandingFor(startedFor: null, pendingFor: null, Fresh));
    }

    [Fact]
    public void StarvationCrossingTheBudget_ReportsOnceAndLatches()
    {
        var decision = UiDispatchHeartbeat.Decide(Over, Fresh, warned: false);

        Assert.True(decision.Starved);
        Assert.False(decision.Recovered);
        Assert.True(decision.Warned, "so the next tick does not say it again");
        Assert.False(UiDispatchHeartbeat.Decide(Over + TimeSpan.FromMinutes(5), Fresh, decision.Warned).Starved);
    }

    [Fact]
    public void TheProbeBeingPickedUpAgain_ReportsRecovery()
    {
        var decision = UiDispatchHeartbeat.Decide(pendingFor: null, Fresh, warned: true);

        Assert.False(decision.Starved);
        Assert.True(decision.Recovered);
        Assert.False(decision.Warned, "let off the hook, so a later starvation is heard again");
    }

    // Starvation that hardens into a blocked thread hands the wait back to the render clock rather than going
    // quiet: the dispatch alarm stays latched, and the stall is reported on its own account.
    [Fact]
    public void StarvationTurningIntoABlockedThread_DoesNotReadAsRecovery()
    {
        var decision = UiDispatchHeartbeat.Decide(Over, sinceHighPriorityPong: Over, warned: true);

        Assert.False(decision.Recovered);
        Assert.True(RenderClockHeartbeat.Decide(
            UiDispatchHeartbeat.RenderClockOutstandingFor(startedFor: null, Over, Over), warned: false).Stalled);
    }

    // The budget seam the view tests drive on: a shortened budget alarms early, and never on a shorter wait.
    [Fact]
    public void TheBudgetCanBeShortenedForATest_WithoutChangingTheRule()
    {
        var shortened = TimeSpan.FromSeconds(3);

        Assert.True(UiDispatchHeartbeat.IsStarved(TimeSpan.FromSeconds(4), Fresh, shortened));
        Assert.False(UiDispatchHeartbeat.IsStarved(TimeSpan.FromSeconds(4), Fresh));
        Assert.False(UiDispatchHeartbeat.IsStarved(TimeSpan.FromSeconds(2), Fresh, shortened));
    }

    // AC-1196 on the isMacOs gate, half one: starvation never reaches it however long it lasts. The app is drawing
    // and taking input, so collapsing a transcript would be damage done on a false reading.
    [Fact]
    public void HoweverLongStarvationLasts_ItNeverReachesThePauseDecision()
    {
        var forever = RenderClockHeartbeat.PauseAfter * 10;
        var starved = UiDispatchHeartbeat.RenderClockOutstandingFor(startedFor: null, forever, Fresh);

        Assert.Null(starved);
        Assert.False(RenderClockHeartbeat.ShouldPauseRenderers(starved, isMacOs: true));
    }
}
