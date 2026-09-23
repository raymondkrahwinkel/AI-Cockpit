using Cockpit.Plugin.Workflows.Engine;

namespace Cockpit.Plugin.Workflows.Tests;

// What a scheduled slot deserves when the clock finally looks at it (#AC-1359, #AC-493). A pure decision: no clock,
// no timer, no restart needed to prove any of this — just the two moments and the grace.
public class ScheduleCatchUpTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(60);

    [Theory]
    // Nothing is due at all — the schedule has no slot at or before `now`.
    [InlineData(null, null, "2026-07-13T09:00:00", ScheduleCatchUpOutcome.Nothing)]
    // A freshly armed flow, or a wiped cache: no marker yet. Seeded, not fired — arming at 09:05 must not run 09:00.
    [InlineData("2026-07-13T09:00:00", null, "2026-07-13T09:05:00", ScheduleCatchUpOutcome.Nothing)]
    // Already handled: the marker has moved past this slot.
    [InlineData("2026-07-13T09:00:00", "2026-07-13T09:00:00", "2026-07-13T09:00:10", ScheduleCatchUpOutcome.Nothing)]
    // A second restart moments after the first: the marker from the first restart already covers this slot.
    [InlineData("2026-07-13T09:00:00", "2026-07-13T09:00:00", "2026-07-13T09:00:40", ScheduleCatchUpOutcome.Nothing)]
    // On time: the watcher noticed within the same minute.
    [InlineData("2026-07-13T09:00:00", "2026-07-13T08:00:00", "2026-07-13T09:00:10", ScheduleCatchUpOutcome.Fire)]
    // Late, but inside the 60-minute grace: a restart that took 15 minutes.
    [InlineData("2026-07-13T09:00:00", "2026-07-13T08:00:00", "2026-07-13T09:15:00", ScheduleCatchUpOutcome.Late)]
    // Outside the grace: down from 08:55 to 10:30 is 90 minutes late.
    [InlineData("2026-07-13T09:00:00", "2026-07-13T08:00:00", "2026-07-13T10:30:00", ScheduleCatchUpOutcome.Missed)]
    public void Decide_WeighsTheSlotAgainstTheMarkerAndTheGrace(string? previousSlot, string? lastHandledSlot, string now, ScheduleCatchUpOutcome expected)
    {
        var decision = ScheduleCatchUp.Decide(_Parse(previousSlot), _Parse(lastHandledSlot), DateTimeOffset.Parse(now), Grace);

        Assert.Equal(expected, decision.Outcome);
    }

    private static DateTimeOffset? _Parse(string? value) => value is null ? null : DateTimeOffset.Parse(value);

    // A `once` trigger has no next occurrence to bootstrap towards, so the watcher passes it an epoch marker
    // instead of null on its first sighting — caught during review: passing null here would have seeded the
    // schedule's one and only slot away as "already handled" without ever running it.
    [Fact]
    public void ADueSlot_WithAnEpochMarker_FiresRatherThanBootstraps()
    {
        var decision = ScheduleCatchUp.Decide(
            DateTimeOffset.Parse("2026-10-01T09:00:00"),
            DateTimeOffset.MinValue,
            DateTimeOffset.Parse("2026-10-01T09:00:10"),
            Grace);

        Assert.Equal(ScheduleCatchUpOutcome.Fire, decision.Outcome);
    }
}
