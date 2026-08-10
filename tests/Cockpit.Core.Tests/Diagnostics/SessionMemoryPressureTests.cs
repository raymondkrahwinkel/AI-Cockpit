using Cockpit.Core.Diagnostics;

namespace Cockpit.Core.Tests.Diagnostics;

/// <summary>
/// When to say — cockpit-wide, by name — that one session is closing in on its own memory cap (AC-692, on top of
/// AC-661's <c>SessionMemoryCap</c>). A sibling to <see cref="MemoryPressureTests"/>: same hysteresis shape, but
/// measured as a share of the session's own cap rather than a share of the machine.
/// </summary>
public class SessionMemoryPressureTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private const long Cap = 8 * Gb; // SessionMemoryCap.DefaultMegabytes

    [Fact]
    public void PastNineTenthsOfTheCap_ItWarns()
    {
        var decision = SessionMemoryPressure.Decide(usedBytes: (long)(0.95 * Cap), capBytes: Cap, warned: false);

        Assert.True(decision.Warn);
        Assert.True(decision.Warned, "so the next sample does not say it again");
    }

    [Fact]
    public void HavingSaidItOnce_ItDoesNotRepeatWhileYouDecide()
    {
        var decision = SessionMemoryPressure.Decide(usedBytes: (long)(0.92 * Cap), capBytes: Cap, warned: true);

        Assert.False(decision.Warn, "a warning every sample is a warning you turn off");
        Assert.True(decision.Warned);
    }

    [Fact]
    public void OnceItHasFallenWellBack_TheNextClimbIsWorthSayingAgain()
    {
        var calm = SessionMemoryPressure.Decide(usedBytes: (long)(0.6 * Cap), capBytes: Cap, warned: true);

        Assert.False(calm.Warn);
        Assert.False(calm.Warned, "it is let off the hook, so a real climb later is heard");

        Assert.True(SessionMemoryPressure.Decide(usedBytes: (long)(0.95 * Cap), capBytes: Cap, calm.Warned).Warn);
    }

    [Fact]
    public void JustDippingUnderTheWarnLine_DoesNotResetIt()
    {
        // Otherwise a session that breathes in and out around the threshold warns you twice a minute.
        Assert.Equal(
            new MemoryPressureDecision(false, true),
            SessionMemoryPressure.Decide(usedBytes: (long)(0.8 * Cap), capBytes: Cap, warned: true));
    }

    [Fact]
    public void AnOrdinarySession_SaysNothing() =>
        Assert.False(SessionMemoryPressure.Decide(usedBytes: (long)(0.3 * Cap), capBytes: Cap, warned: false).Warn);

    [Fact]
    public void AnUncappedSession_SaysNothing() =>
        // Zero/negative means nothing was resolved to cap this session against — a share of no limit is not a fact.
        Assert.False(SessionMemoryPressure.Decide(usedBytes: 20 * Gb, capBytes: 0, warned: false).Warn);

    [Fact]
    public void ASessionWithNothingMeasured_SaysNothing() =>
        Assert.False(SessionMemoryPressure.Decide(usedBytes: 0, capBytes: Cap, warned: false).Warn);
}
