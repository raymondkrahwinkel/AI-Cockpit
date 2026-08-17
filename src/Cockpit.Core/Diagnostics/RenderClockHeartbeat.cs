namespace Cockpit.Core.Diagnostics;

// AC-882: hysteresis for the render-clock probe, same warn-once/calm-later shape as UiThreadHeartbeat — a pure
// function so it is testable without a render loop. Avalonia 12 parks the render clock on every platform once no
// render-loop task wants another tick, and a commit is what wakes it again (ServerCompositor.EnqueueBatch calls
// IRenderLoop.Wakeup). A probe commit that never reports Processed therefore means the clock stopped and did not
// come back — the shape a stalled macOS display link takes. Posting the probe is the caller's job; see
// DiagnosticsBackgroundService._StartRenderClockProbe.
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
