namespace Cockpit.Core.Diagnostics;

// When to warn — cockpit-wide, by name, not just on the session's own pane — that a single session is closing in
// on its own memory cap (`Cockpit.Core.Sessions.SessionMemoryCap`, AC-661). A sibling to `MemoryPressure`, which
// watches the cockpit's whole tree against the machine; this one watches one session's tree against the cap that
// already decides when the OS cuts that session off.
//
// AC-661 already puts a warning on the session's own bar at 80% of the cap (`SessionPanelViewModel.
// ReportMemoryAgainstCap`), but that is only seen while looking at that pane, and it has no hysteresis of its own
// — it raises and clears on the same line, so a session hovering right at 80% could flicker it. This is the
// louder, cockpit-wide signal for the same climb: warn once on the way up, stay quiet until it has fallen back
// well below the line, same shape as `MemoryPressure`.
public static class SessionMemoryPressure
{
    // Later than the pane's own 80% marker on purpose — the toast is the interruption, the pane warning is the
    // detail you see when you look. Firing them at the same share would make the toast redundant with what the
    // pane already showed.
    public const double WarnAtShare = 0.9;

    // Stay quiet again once it has fallen back below this — comfortably under the pane's own 80% clear line, so
    // letting the toast off the hook does not race the pane's warning turning back on a moment later.
    public const double CalmAtShare = 0.7;

    // Whether to warn now about this one session's approach to its own cap. `warned` is whether the operator has
    // already been told about *this session* and not yet let off the hook — the caller keeps that per session, not
    // as one flag for the whole cockpit, so one loud session does not silence the warning for the next one.
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

        if (!warned && share >= WarnAtShare)
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
