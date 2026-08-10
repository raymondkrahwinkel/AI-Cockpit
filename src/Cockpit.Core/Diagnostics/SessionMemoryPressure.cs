namespace Cockpit.Core.Diagnostics;

// Warns, cockpit-wide and by name with a kill button, when a session crosses its own memory cap
// (`SessionMemoryCap`, AC-661) — the replacement for the automatic kill AC-692 removed, with the same
// warn-once/calm-later hysteresis shape as `MemoryPressure`.
public static class SessionMemoryPressure
{
    // Not a tuned fraction like `MemoryPressure.WarnAtShare` — this is the cap itself, the exact point that used
    // to trigger the automatic kill. The notice replaces the kill; it does not move the line.
    public const double WarnAtShare = 1.0;

    // Stay quiet again once it has fallen back comfortably under the cap — otherwise a session hovering right at
    // the line flips the notice on and off every sample.
    public const double CalmAtShare = 0.9;

    // `warned` is tracked per session, not globally, so one loud session does not silence the next (AC-692).
    // `capBytes` <= 0 means uncapped — nothing to warn about.
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
