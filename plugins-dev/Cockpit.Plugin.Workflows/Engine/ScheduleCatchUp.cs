namespace Cockpit.Plugin.Workflows.Engine;

// What a scheduled slot deserves when the clock finally looks at it (#AC-1359, #AC-493): a slot seen late — after a
// restart, after the laptop slept — must say so rather than pretend it was on time, and one seen too late is
// dropped rather than run: silently shifting it is exactly what AC-493's honesty rule forbids.
public enum ScheduleCatchUpOutcome
{
    // Nothing due, or the slot is already handled — the marker has moved past it.
    Nothing,

    // The slot fell inside the tick that noticed it: an on-time fire.
    Fire,

    // The slot went unseen but still lies inside the grace: run it once, and say it was late.
    Late,

    // The slot went unseen and lies outside the grace: log it, do not run it.
    Missed,
}

// A pure decision: no clock of its own, no timer, so nothing here needs a real minute to pass to be tested.
internal readonly record struct ScheduleCatchUp(ScheduleCatchUpOutcome Outcome, DateTimeOffset Slot)
{
    // How close a slot must be to `now` to count as on time rather than a catch-up. One tick's worth of lag is not
    // lateness — it is the ordinary gap between the clock landing on a slot and this watcher noticing.
    private static readonly TimeSpan OnTimeWindow = TimeSpan.FromMinutes(1);

    // `previousSlot` is the latest scheduled moment at or before `now` (see `Schedule.Previous`), or null when
    // nothing is due yet. `lastHandledSlot` is the marker: the last slot this trigger already dealt with, or null
    // for a trigger that has never been armed (or whose cache was wiped).
    public static ScheduleCatchUp Decide(DateTimeOffset? previousSlot, DateTimeOffset? lastHandledSlot, DateTimeOffset now, TimeSpan grace)
    {
        if (previousSlot is not { } slot)
        {
            return new ScheduleCatchUp(ScheduleCatchUpOutcome.Nothing, default);
        }

        // No marker at all: a freshly armed flow, or a wiped cache. The marker is seeded on this slot without a
        // catch-up run — arming a flow at 09:05 must not fire the 09:00 slot it never promised to run.
        if (lastHandledSlot is not { } handled)
        {
            return new ScheduleCatchUp(ScheduleCatchUpOutcome.Nothing, slot);
        }

        if (slot <= handled)
        {
            return new ScheduleCatchUp(ScheduleCatchUpOutcome.Nothing, handled);
        }

        var lag = now - slot;
        var outcome = lag <= OnTimeWindow
            ? ScheduleCatchUpOutcome.Fire
            : lag <= grace
                ? ScheduleCatchUpOutcome.Late
                : ScheduleCatchUpOutcome.Missed;

        return new ScheduleCatchUp(outcome, slot);
    }
}
