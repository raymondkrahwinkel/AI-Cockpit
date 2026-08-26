using Cockpit.Core.Diagnostics;

namespace Cockpit.Core.Tests.Diagnostics;

/// <summary>
/// Tests <see cref="SessionPressureAlarm"/>, the AC-1060 warning that has to arrive minutes before an oomd kill.
/// The point of every case here is that it holds for twenty seconds, since one sample is what makes it noise.
/// </summary>
public class SessionPressureAlarmTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 25, 9, 8, 0, TimeSpan.Zero);

    private const double Stalling = SessionPressureAlarm.WarnAboveAvg10 + 5;

    [Fact]
    public void AShortPeak_SaysNothing()
    {
        // A build starting is a burst of allocation, and avg10 climbs for it. That is not the run-up to a kill.
        var first = SessionPressureAlarm.Decide(Stalling, Start, new SessionPressureState(null, false));
        var stillHigh = SessionPressureAlarm.Decide(Stalling, Start.AddSeconds(10), first.Next);
        var over = SessionPressureAlarm.Decide(1.0, Start.AddSeconds(15), stillHigh.Next);

        Assert.False(first.Warn);
        Assert.False(stillHigh.Warn);
        Assert.False(over.Warn);
        Assert.Null(over.Next.RisingSince);
    }

    [Fact]
    public void HeldForTwentySeconds_ItWarnsOnce()
    {
        var first = SessionPressureAlarm.Decide(Stalling, Start, new SessionPressureState(null, false));
        var crossing = SessionPressureAlarm.Decide(Stalling, Start + SessionPressureAlarm.Sustained, first.Next);
        var after = SessionPressureAlarm.Decide(Stalling, Start.AddMinutes(1), crossing.Next);

        Assert.True(crossing.Warn, "this is the window the two kills of 2026-08-25 left open for minutes");
        Assert.False(after.Warn, "a warning every sample is a warning nobody reads");
        Assert.True(after.Next.Warned);
    }

    [Fact]
    public void AStallThatBreaks_StartsItsTwentySecondsAgain()
    {
        var first = SessionPressureAlarm.Decide(Stalling, Start, new SessionPressureState(null, false));
        var broken = SessionPressureAlarm.Decide(1.0, Start.AddSeconds(15), first.Next);
        var resumed = SessionPressureAlarm.Decide(Stalling, Start.AddSeconds(16), broken.Next);

        // "for > 20s" is oomd's own test, and it means without a break — not twenty seconds added up.
        Assert.False(SessionPressureAlarm.Decide(Stalling, Start.AddSeconds(25), resumed.Next).Warn);
        Assert.True(SessionPressureAlarm.Decide(Stalling, Start.AddSeconds(36), resumed.Next).Warn);
    }

    [Fact]
    public void BetweenCalmAndStalling_AStandingWarningStays()
    {
        // Still no place for a session to sit: clearing here would take the notice away while the danger has not
        // passed, and the operator would have nothing on the bar at the moment oomd decides.
        var middle = (SessionPressureAlarm.WarnAboveAvg10 + SessionPressureAlarm.CalmAtAvg10) / 2;

        var decision = SessionPressureAlarm.Decide(middle, Start, new SessionPressureState(null, true));

        Assert.False(decision.Warn);
        Assert.True(decision.Next.Warned);
    }

    [Fact]
    public void OnceItIsProperlyQuiet_TheNextClimbIsHeard()
    {
        var calm = SessionPressureAlarm.Decide(0.0, Start, new SessionPressureState(null, true));

        Assert.False(calm.Next.Warned, "let off the hook, so a real climb later is worth saying again");

        var climbing = SessionPressureAlarm.Decide(Stalling, Start.AddMinutes(5), calm.Next);
        Assert.True(SessionPressureAlarm.Decide(Stalling, Start.AddMinutes(5) + SessionPressureAlarm.Sustained, climbing.Next).Warn);
    }

    [Fact]
    public void AnIdleSession_SaysNothing() =>
        // Every live session cgroup on the machine this was measured on reads 0.00 while working normally.
        Assert.False(SessionPressureAlarm.Decide(0.0, Start, new SessionPressureState(null, false)).Warn);
}
