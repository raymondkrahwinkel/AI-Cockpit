namespace Cockpit.App;

// Keeps all usage indicators on the same theme tokens and escalation points. The provider owns the threshold
// because it knows what its window means; both colour steps derive from that one value to prevent drift
// (AC-229/AC-232).
internal static class UsageSeverity
{
    // Where a figure lands when the host has no declared threshold to go on — a provider that reports a number but
    // never said when it matters.
    public const double FallbackThreshold = 85;

    // The theme brush resource key for a usage percentage against the threshold declared for its signal.
    public static string BrushKeyFor(double percent, double? threshold = null)
    {
        var warnAt = threshold ?? FallbackThreshold;

        return percent >= CriticalAt(warnAt) ? "CockpitStatusErrorBrush"
            : percent >= warnAt ? "CockpitStatusWaitingBrush"
            : "CockpitTextSecondaryBrush";
    }

    // Where amber turns red: halfway from the threshold to full. Derived rather than declared so a provider sets
    // one number, and a signal that warns at 90 still has somewhere left to escalate to.
    public static double CriticalAt(double threshold) => threshold + ((100 - threshold) / 2);
}
