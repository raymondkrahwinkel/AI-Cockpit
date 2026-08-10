namespace Cockpit.Core.Diagnostics;

// When to raise the top-level, cockpit-wide notice — the one with the kill button — that a session has actually
// gone over its own memory cap (`Cockpit.Core.Sessions.SessionMemoryCap`, AC-661). AC-692: Cockpit no longer kills
// a session for this on its own; `PollingMemoryLimiter` (and its per-platform siblings) stopped doing that, and
// this is the replacement — the operator decides, this just decides when to ask.
//
// AC-661 already puts an early warning on the session's own bar at 80% of the cap (`SessionPanelViewModel.
// ReportMemoryAgainstCap`), seen only while looking at that pane. This is the louder, named, cockpit-wide signal
// for the moment the cap is actually crossed — warn once on the way up, stay quiet until it has fallen back well
// below the line, same hysteresis shape as `MemoryPressure`.
public static class SessionMemoryPressure
{
    // Not a tuned fraction like `MemoryPressure.WarnAtShare` — this is the cap itself, the exact point that used
    // to trigger the automatic kill. The notice replaces the kill; it does not move the line.
    public const double WarnAtShare = 1.0;

    // Stay quiet again once it has fallen back comfortably under the cap — otherwise a session hovering right at
    // the line flips the notice on and off every sample.
    public const double CalmAtShare = 0.9;

    // Whether to raise the notice now for this one session. `warned` is whether the operator has already been
    // told about *this session* and not yet let off the hook — the caller keeps that per session, not as one flag
    // for the whole cockpit, so one loud session does not silence the notice for the next one.
    //
    // `usedBytes`: what this session's tree is holding.
    // `capBytes`: this session's own memory cap (`SessionMemoryCap.ResolveBytes`). Zero or negative means
    // uncapped, which reads as nothing to warn about — a share of no limit is not a fact.
    public static MemoryPressureDecision Decide(long usedBytes, long capBytes, bool warned)
    {
        if (capBytes <= 0 || usedBytes <= 0)
        {
            return new MemoryPressureDecision(false, warned);
        }

        var share = (double)usedBytes / capBytes;

        if (!warned && share > WarnAtShare)
        {
            return new MemoryPressureDecision(true, true);
        }

        if (warned && share <= CalmAtShare)
        {
            return new MemoryPressureDecision(false, false);
        }

        return new MemoryPressureDecision(false, warned);
    }
}
