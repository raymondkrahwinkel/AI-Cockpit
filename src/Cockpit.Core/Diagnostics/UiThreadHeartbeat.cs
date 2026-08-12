namespace Cockpit.Core.Diagnostics;

// AC-718: hysteresis for the UI-thread freeze heartbeat, in the same warn-once/calm-later shape as
// SessionMemoryPressure.Decide — a pure function so the behavior is testable without an actual frozen UI thread.
// The background thread that pings the dispatcher only calls this once it has seen a first successful pong
// (arming happens there, not here): before that the dispatcher has not started its loop yet, and every value
// here would read as an infinite hang.
public static class UiThreadHeartbeat
{
    // Above this since the last pong, the dispatcher is not keeping up — warn.
    public static readonly TimeSpan WarnAfter = TimeSpan.FromSeconds(5);

    // Below this, it is comfortably caught up again — recover. Sits well under WarnAfter so a pong that lands
    // right after the warning does not immediately flip back and forth.
    public static readonly TimeSpan CalmBelow = TimeSpan.FromSeconds(1);

    public static UiThreadHeartbeatDecision Decide(TimeSpan sinceLastPong, bool warned)
    {
        if (!warned && sinceLastPong > WarnAfter)
        {
            return new UiThreadHeartbeatDecision(Warn: true, Recovered: false, Warned: true);
        }

        if (warned && sinceLastPong < CalmBelow)
        {
            return new UiThreadHeartbeatDecision(Warn: false, Recovered: true, Warned: false);
        }

        return new UiThreadHeartbeatDecision(Warn: false, Recovered: false, Warned: warned);
    }
}

public sealed record UiThreadHeartbeatDecision(bool Warn, bool Recovered, bool Warned);
