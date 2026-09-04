namespace Cockpit.Core.Notifications;

// AC-1273: pure kernel behind the safety net under a session whose background shell finished without the provider
// ever saying so. Measured: the `claude` CLI injects its own `<task-notification>` and opens a turn within a second
// of the task exiting — but not always. Of 169 background tasks that ran over a minute, exited with a real code and
// were never read out by the agent itself, 78 got no notification; the session that ended its turn to wait then
// stands still until something outside it happens to send a prompt.
//
// Deliberately hard to trigger. The cockpit stands outside this today and the provider gets it right most of the
// time, so the net must do nothing at all whenever the provider does its own job: the grace is two orders of
// magnitude wider than a working delivery, and any sign of life since the shell ended — a turn the provider opened,
// the operator typing, anything that stamps the session's last activity — calls it off. A net that starts producing
// turns of its own is worse than the gap it covers.
public static class StrandedBackgroundTaskDecision
{
    // How long a session may stand still after its last background shell ended before the cockpit says something.
    // A delivery that works lands in under a second (measured across five spawn shapes), so nothing healthy comes
    // anywhere near this.
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromMinutes(2);

    // `isFinished`: the turn is over and nothing is pending — the same wakeable set a peer's urgent notify may
    // interrupt (`WorkspaceAgentGateway`), so this can never land on top of a turn that is already running.
    // `shellsEndedAt`: when the session's last background shell left the provider's task list.
    // `lastActivity`: the session's own last status change. Later than `shellsEndedAt` means it already moved on by
    // itself — the provider's notification arrived after all, or the operator got there first.
    // `grace`: zero or less turns the net off.
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
