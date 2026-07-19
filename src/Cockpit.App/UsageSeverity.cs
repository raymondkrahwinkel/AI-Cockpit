namespace Cockpit.App;

/// <summary>
/// Shared usage thresholds and the theme brush key each severity resolves to, so the header's usage pill and
/// <see cref="Controls.LimitBar"/>'s fill escalate at the same points and can never drift apart. The tokens are
/// the same ones the session status dots use, so a palette change carries.
/// </summary>
internal static class UsageSeverity
{
    /// <summary>At or above this a usage figure warns (amber): a session past two-thirds is worth noticing, not interrupting.</summary>
    public const double WarnAbove = 60;

    /// <summary>At or above this it is critical (red): close enough that the operator should decide rather than discover mid-turn.</summary>
    public const double CriticalAbove = 85;

    /// <summary>The theme brush resource key for a usage percentage's severity.</summary>
    public static string BrushKeyFor(double percent) =>
        percent >= CriticalAbove ? "CockpitStatusErrorBrush"
        : percent >= WarnAbove ? "CockpitStatusWaitingBrush"
        : "CockpitTextSecondaryBrush";
}
