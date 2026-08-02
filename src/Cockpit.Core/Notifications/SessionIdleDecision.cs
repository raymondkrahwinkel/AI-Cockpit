namespace Cockpit.Core.Notifications;

// Pure kernel deciding when a finished session goes quiet: a session that completed its turn and has done
// nothing since drops back to idle once `threshold` has passed. Only a *finished*
// session does — one that is busy, waiting on a permission or asking for attention is not idle no matter how
// long it has been sitting there, since the wait is the work. Free of timers and view models so the rule stays
// unit-testable, like `PresenceDecision`.
public static class SessionIdleDecision
{
    // Default time a finished session stays "done" before it counts as idle.
    public static readonly TimeSpan DefaultIdleThreshold = TimeSpan.FromMinutes(5);

    // `isFinished`: The session completed its last turn and nothing is pending (status Done).
    // `lastActivity`: When the session last did anything (a turn, a tool call, a status change).
    // `now`: The current time.
    // `threshold`: How long a finished session must be quiet. `TimeSpan.Zero` or less turns the rule off.
    public static bool BecomesIdle(bool isFinished, DateTimeOffset lastActivity, DateTimeOffset now, TimeSpan threshold)
    {
        if (!isFinished || threshold <= TimeSpan.Zero)
        {
            return false;
        }

        return now - lastActivity >= threshold;
    }
}
