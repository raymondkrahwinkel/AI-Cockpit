namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// What the currently selected session is spending right now (AC-54), as a plugin can read it through
/// <see cref="ICockpitSessionObserver.ActiveSessionUsage"/>: how full its context window is and the usage
/// windows it reports (5h / wk / …), plus the label of the profile it runs under so a contribution can keep the
/// figures per profile. It is the plugin-facing mirror of what the session header already renders — the same
/// numbers, exposed as a read surface rather than a header pill — so a widget can chart usage over time without
/// the host knowing what the widget is.
/// <para>
/// A polled snapshot, not a running series: the host hands out the latest value and raises
/// <see cref="ICockpitSessionObserver.ActiveSessionUsageChanged"/> when it moves, and the plugin decides what to
/// keep. Every figure is optional on purpose — a subscription reports rate limits only after the first response,
/// and the context percentage is silent before the first turn and right after a <c>/compact</c> — so a missing
/// value is a silence to skip, never a zero to record.
/// </para>
/// </summary>
/// <param name="ProfileLabel">
/// The label of the profile the session was started under, or <see langword="null"/> when it is not yet known.
/// The only identifying handle a profile carries (there is no stable profile id), so it is what a per-profile
/// history groups on; renaming a profile therefore starts a fresh group.
/// </param>
/// <param name="ContextUsedPercent">How full the context window is, 0-100, or <see langword="null"/> before the provider reports it.</param>
/// <param name="RateLimits">The usage windows the session reports, each self-labelled (e.g. "5h", "wk"); empty when it reports none.</param>
public sealed record SessionUsageSnapshot(
    string? ProfileLabel,
    double? ContextUsedPercent,
    IReadOnlyList<PluginRateLimitWindow> RateLimits)
{
    /// <summary>Whether there is any usage figure worth recording — a context percentage, or at least one window — so a consumer can skip a silent snapshot rather than storing a row of nulls.</summary>
    public bool HasAny => ContextUsedPercent is not null || RateLimits.Count > 0;
}
