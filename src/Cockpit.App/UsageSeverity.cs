namespace Cockpit.App;

/// <summary>
/// How full is full enough to say something, and the theme brush key each severity resolves to, so the header's
/// usage pill, <see cref="Controls.LimitBar"/>'s fill and the warning in the session bar all escalate at the same
/// point and can never drift apart. The tokens are the same ones the session status dots use, so a palette change
/// carries.
/// <para>
/// The threshold comes from the provider that declared the signal (AC-229/AC-232), not from a constant here: the
/// provider knows what its window means and therefore when it is worth interrupting for. One number per signal,
/// with the two colour steps derived from it — a second configurable number is a second thing that can disagree
/// with the first.
/// </para>
/// </summary>
internal static class UsageSeverity
{
    /// <summary>Where a figure lands when the host has no declared threshold to go on — a provider that reports a number but never said when it matters.</summary>
    public const double FallbackThreshold = 85;

    /// <summary>The theme brush resource key for a usage percentage against the threshold declared for its signal.</summary>
    public static string BrushKeyFor(double percent, double? threshold = null)
    {
        var warnAt = threshold ?? FallbackThreshold;

        return percent >= CriticalAt(warnAt) ? "CockpitStatusErrorBrush"
            : percent >= warnAt ? "CockpitStatusWaitingBrush"
            : "CockpitTextSecondaryBrush";
    }

    /// <summary>
    /// Where amber turns red: halfway from the threshold to full. Derived rather than declared so a provider sets
    /// one number, and a signal that warns at 90 still has somewhere left to escalate to.
    /// </summary>
    public static double CriticalAt(double threshold) => threshold + ((100 - threshold) / 2);
}
