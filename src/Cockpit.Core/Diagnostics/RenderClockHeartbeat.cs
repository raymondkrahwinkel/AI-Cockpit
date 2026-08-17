namespace Cockpit.Core.Diagnostics;

// AC-882: hysteresis for the render-clock probe, same warn-once/calm-later shape as UiThreadHeartbeat. A probe
// commit that never reports Processed means the render clock stopped and did not come back.
public static class RenderClockHeartbeat
{
    // A probe on a healthy pipeline completes within a frame or two. Three orders of magnitude of headroom, so a
    // loaded machine, a slow GPU or a commit queued behind a long streaming burst never reads as a stall.
    public static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(15);

    // A null probeInFlightFor means no probe is outstanding: either none has been posted, or the last one came
    // back. Both are the healthy case, and both are what ends a stall.
    public static RenderClockHeartbeatDecision Decide(TimeSpan? probeInFlightFor, bool warned)
    {
        if (!warned && probeInFlightFor > StallAfter)
        {
            return new RenderClockHeartbeatDecision(Stalled: true, Resumed: false, Warned: true);
        }

        if (warned && probeInFlightFor is null)
        {
            return new RenderClockHeartbeatDecision(Stalled: false, Resumed: true, Warned: false);
        }

        return new RenderClockHeartbeatDecision(Stalled: false, Resumed: false, Warned: warned);
    }
}

public sealed record RenderClockHeartbeatDecision(bool Stalled, bool Resumed, bool Warned);
