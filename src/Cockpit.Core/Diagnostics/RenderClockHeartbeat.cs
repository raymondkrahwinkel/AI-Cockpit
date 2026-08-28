namespace Cockpit.Core.Diagnostics;

// AC-882: hysteresis for the render-clock probe, same warn-once/calm-later shape as UiThreadHeartbeat. A probe
// commit that never reports Processed means the render clock stopped and did not come back.
public static class RenderClockHeartbeat
{
    // A probe on a healthy pipeline completes within a frame or two. Three orders of magnitude of headroom, so a
    // loaded machine, a slow GPU or a commit queued behind a long streaming burst never reads as a stall.
    public static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(15);

    // AC-883: writing a log line and collapsing a transcript the operator may be reading are not the same act, so
    // they do not share a threshold. Four times StallAfter, and one probe is outstanding at a time, so crossing it
    // means a single forced commit has gone unprocessed for a full minute — a clock that stopped, not a busy machine.
    public static readonly TimeSpan PauseAfter = TimeSpan.FromMinutes(1);

    // A null probeInFlightFor means no probe is outstanding: either none has been posted, or the last one came
    // back. Both are the healthy case, and both are what ends a stall. stallAfter overrides the budget so a test
    // can drive this at seconds instead of waiting a real quarter-minute per case (AC-1196).
    public static RenderClockHeartbeatDecision Decide(TimeSpan? probeInFlightFor, bool warned, TimeSpan? stallAfter = null)
    {
        if (!warned && probeInFlightFor > (stallAfter ?? StallAfter))
        {
            return new RenderClockHeartbeatDecision(Stalled: true, Resumed: false, Warned: true);
        }

        if (warned && probeInFlightFor is null)
        {
            return new RenderClockHeartbeatDecision(Stalled: false, Resumed: true, Warned: false);
        }

        return new RenderClockHeartbeatDecision(Stalled: false, Resumed: false, Warned: warned);
    }

    // Whether panes should stop producing composition churn because the render clock is no longer processing it.
    // probeInFlightFor is how long the outstanding forced commit has gone unprocessed, null when none is out.
    // isMacOs is passed in rather than read here so both branches are testable.
    public static bool ShouldPauseRenderers(TimeSpan? probeInFlightFor, bool isMacOs) =>
        // Windows and X11 drive the clock from a software sleep loop the OS cannot take away, and WindowState.
        // Minimized is full coverage of pausing there — so their existing, working behaviour stays untouched.
        // AC-1196 reconsidered this gate and left it: see the caller, which is where the answer lives.
        isMacOs && probeInFlightFor > PauseAfter;
}

public sealed record RenderClockHeartbeatDecision(bool Stalled, bool Resumed, bool Warned);
