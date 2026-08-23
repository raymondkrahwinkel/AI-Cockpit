namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// What the currently selected session is spending right now (AC-54), as a plugin can read it through
/// <see cref="ICockpitSessionObserver.ActiveSessionUsage"/>: how full its context window is and the usage
/// windows it reports, plus the label of the profile it runs under.
/// </summary>
/// <remarks>
/// A polled snapshot, not a running series: the host hands out the latest value and raises
/// <see cref="ICockpitSessionObserver.ActiveSessionUsageChanged"/> when it moves. Every figure is optional — a
/// missing value is a silence to skip, never a zero to record.
/// </remarks>
/// <param name="ProfileLabel">
/// The label of the profile the session was started under, or <see langword="null"/> when it is not yet known.
/// The only identifying handle a profile carries (there is no stable profile id), so it is what a per-profile
/// history groups on; renaming a profile therefore starts a fresh group.
/// </param>
/// <param name="ContextUsedPercent">
/// How full the context window is, 0-100, or <see langword="null"/> before the provider reports it.
/// </param>
/// <param name="RateLimits">
/// The usage windows the session reports, each self-labelled (e.g. "5h", "wk"); empty when it reports none.
/// </param>
public sealed record SessionUsageSnapshot(
    string? ProfileLabel,
    double? ContextUsedPercent,
    IReadOnlyList<PluginRateLimitWindow> RateLimits)
{
    /// <summary>
    /// Whether there is any usage figure worth recording — a context percentage, or at least one window — so a consumer can skip a silent snapshot rather than storing a row of nulls.
    /// </summary>
    public bool HasAny => ContextUsedPercent is not null || RateLimits.Count > 0;
}
