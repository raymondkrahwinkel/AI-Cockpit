namespace Cockpit.Core.Notifications;

// AC-1273: pure kernel behind the safety net under a session whose background shell finished without the provider
// saying so. Of 169 tasks that ran over a minute, exited with a real code and were never read out by the agent, 78
// got no notification — and a session that ended its turn to wait then stands still until something outside pokes it.

// Deliberately hard to trigger. The provider gets this right most of the time, so any sign of life since the shell
// ended calls the net off and the grace is far wider than a working delivery ever needs. A net that starts producing
// turns of its own would be worse than the gap it covers.
public static class StrandedBackgroundTaskDecision
{
    // How long a session may stand still after its last background shell ended before the cockpit says something.
    // A delivery that works lands in under a second (measured across five spawn shapes), so nothing healthy comes
    // anywhere near this.
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromMinutes(2);

    // `isFinished`: the turn is over and nothing pending — the same wakeable set an urgent notify from a peer may
    // interrupt, so this can never land on a turn already running. `lastActivity` later than `shellsEndedAt` means it
    // moved on by itself: the notification arrived after all, or the operator got there first. Grace zero turns it off.
    public static bool IsStranded(
        bool isFinished,
        DateTimeOffset shellsEndedAt,
        DateTimeOffset lastActivity,
        DateTimeOffset now,
        TimeSpan grace)
    {
        if (!isFinished || grace <= TimeSpan.Zero || lastActivity > shellsEndedAt)
        {
            return false;
        }

        return now - shellsEndedAt >= grace;
    }
}
