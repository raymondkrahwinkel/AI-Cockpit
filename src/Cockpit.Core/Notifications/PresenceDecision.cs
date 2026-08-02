namespace Cockpit.Core.Notifications;

// Pure presence-decision kernel, deliberately free of any OS/P-Invoke so it is unit-testable:
// the OS-specific detector feeds it the measured idle time and lock state, this decides
// `PresenceState`. "Away" = locked, or idle at/beyond the threshold.
public static class PresenceDecision
{
    public static PresenceState Decide(TimeSpan idle, bool isLocked, TimeSpan idleThreshold)
    {
        if (isLocked)
        {
            return PresenceState.Away;
        }

        return idle >= idleThreshold ? PresenceState.Away : PresenceState.Present;
    }
}
