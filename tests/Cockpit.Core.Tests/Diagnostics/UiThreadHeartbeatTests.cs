using Cockpit.Core.Diagnostics;

namespace Cockpit.Core.Tests.Diagnostics;

/// <summary>
/// Tests <see cref="UiThreadHeartbeat"/> — same hysteresis shape as <see cref="SessionMemoryPressureTests"/>,
/// measured against time since the last successful dispatcher pong instead of a memory share.
/// </summary>
public class UiThreadHeartbeatTests
{
    private static readonly TimeSpan Warn = UiThreadHeartbeat.WarnAfter;
    private static readonly TimeSpan Calm = UiThreadHeartbeat.CalmBelow;

    [Fact]
    public void BelowTheThreshold_SaysNothing() =>
        Assert.False(UiThreadHeartbeat.Decide(sinceLastPong: TimeSpan.FromSeconds(2), warned: false).Warn);

    [Fact]
    public void PastTheThreshold_ItWarns()
    {
        var decision = UiThreadHeartbeat.Decide(sinceLastPong: Warn + TimeSpan.FromSeconds(1), warned: false);

        Assert.True(decision.Warn);
        Assert.False(decision.Recovered);
        Assert.True(decision.Warned, "so the next tick does not say it again");
    }

    [Fact]
    public void RightAtTheThreshold_NotYetOverIt_SaysNothing() =>
        Assert.False(UiThreadHeartbeat.Decide(sinceLastPong: Warn, warned: false).Warn);

    [Fact]
    public void HavingWarnedOnce_ItDoesNotRepeatEveryTick()
    {
        var decision = UiThreadHeartbeat.Decide(sinceLastPong: Warn + TimeSpan.FromSeconds(10), warned: true);

        Assert.False(decision.Warn, "a warning every tick is a warning you turn off");
        Assert.False(decision.Recovered);
        Assert.True(decision.Warned);
    }

    [Fact]
    public void StillHungButNotYetCalm_NeitherWarnsNorRecovers() =>
        // Between CalmBelow and WarnAfter while already warned: still hung, but not comfortably back yet.
        Assert.Equal(
            new UiThreadHeartbeatDecision(false, false, true),
            UiThreadHeartbeat.Decide(sinceLastPong: TimeSpan.FromSeconds(2), warned: true));

    [Fact]
    public void OnceItDropsBelowCalm_ItRecovers()
    {
        var decision = UiThreadHeartbeat.Decide(sinceLastPong: Calm - TimeSpan.FromMilliseconds(1), warned: true);

        Assert.False(decision.Warn);
        Assert.True(decision.Recovered);
        Assert.False(decision.Warned, "let off the hook, so a real hang later is heard again");
    }

    [Fact]
    public void AfterRecovering_ANewHangCanWarnAgain()
    {
        var recovered = UiThreadHeartbeat.Decide(sinceLastPong: TimeSpan.Zero, warned: true);
        Assert.False(recovered.Warned);

        Assert.True(UiThreadHeartbeat.Decide(sinceLastPong: Warn + TimeSpan.FromSeconds(1), recovered.Warned).Warn);
    }

    [Fact]
    public void ANormalTick_SaysNothing() =>
        Assert.False(UiThreadHeartbeat.Decide(sinceLastPong: TimeSpan.Zero, warned: false).Warn);
}
