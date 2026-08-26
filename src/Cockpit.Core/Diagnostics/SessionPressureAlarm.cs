namespace Cockpit.Core.Diagnostics;

// Where a session sits in the run-up to an oomd kill, and for how long it has been there.
public readonly record struct SessionPressureState(DateTimeOffset? RisingSince, bool Warned);

// Whether to say something now, and the state to keep for the next sample.
public readonly record struct SessionPressureDecision(bool Warn, SessionPressureState Next);

// AC-1060: warns while a session's own cgroup is stalling on memory, minutes before `systemd-oomd` kills the
// whole group. Same warn-once/calm-later shape as `SessionMemoryPressure`, on stall time rather than bytes.
public static class SessionPressureAlarm
{
    // A session doing ordinary work sits at 0.00; the two groups oomd killed on 2026-08-25 had been reclaiming
    // against their own `memory.high` for minutes on end. Twenty is far above quiet and far below the 90.80
    // the kill itself was decided on, so the warning lands inside that run-up rather than on top of the kill.
    public const double WarnAboveAvg10 = 20.0;

    // Quiet again below this, so a session hovering at the line does not flip the warning every sample.
    public const double CalmAtAvg10 = 10.0;

    // Not one sample: `avg10` climbs on a short healthy peak too. Twenty seconds sustained is what oomd itself
    // requires before acting, so a warning that outlives it is the same event seen earlier — not noise.
    public static readonly TimeSpan Sustained = TimeSpan.FromSeconds(20);

    // `state` is per session, not one for the cockpit: a loud session must not cost the next one its warning.
    public static SessionPressureDecision Decide(double avg10, DateTimeOffset now, SessionPressureState state)
    {
        if (avg10 > WarnAboveAvg10)
        {
            var risingSince = state.RisingSince ?? now;
            var warn = !state.Warned && now - risingSince >= Sustained;
            return new SessionPressureDecision(warn, new SessionPressureState(risingSince, state.Warned || warn));
        }

        // Below the line the clock restarts, but a warning already given stands until the pressure is properly
        // gone — between calm and warn is still no place for a session to sit.
        return avg10 <= CalmAtAvg10
            ? new SessionPressureDecision(false, new SessionPressureState(null, false))
            : new SessionPressureDecision(false, new SessionPressureState(null, state.Warned));
    }
}
