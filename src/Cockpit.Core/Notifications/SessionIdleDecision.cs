namespace Cockpit.Core.Notifications;

// Pure kernel deciding when a finished session goes quiet: it drops back to idle once
// `threshold` has passed since last activity. Only a *finished* session does — busy or
// waiting-on-attention is never idle, since the wait is the work.
public static class SessionIdleDecision
{
    // Default time a finished session stays "done" before it counts as idle.
    public static readonly TimeSpan DefaultIdleThreshold = TimeSpan.FromMinutes(5);

    // `isFinished`: session completed its last turn, nothing pending (status Done).
    // `threshold`: how long a finished session must be quiet; zero or less turns the rule off.
    public static bool BecomesIdle(bool isFinished, DateTimeOffset lastActivity, DateTimeOffset now, TimeSpan threshold)
    {
        if (!isFinished || threshold <= TimeSpan.Zero)
        {
            return false;
        }

        return now - lastActivity >= threshold;
    }
}
