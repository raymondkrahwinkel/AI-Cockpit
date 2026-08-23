namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// A provider's live status feed (#45 D7) — how full the context window is and the usage windows it reports, the
/// plugin-facing source the host's session header renders its limit bars from.
/// </summary>
/// <remarks>
/// A driver polls its provider and exposes the latest snapshot here; the host adapter maps it to the core's status
/// model. The windows are a self-labelled list rather than a fixed five-hour/weekly pair: a provider reports the
/// windows it has, each carrying its own <see cref="PluginRateLimitWindow.Label"/>, and the header renders them in
/// order.
/// </remarks>
/// <param name="ContextUsedPercent">
/// How full the context window is, 0-100, or <see langword="null"/> before the provider reports it.
/// </param>
/// <param name="RateLimits">
/// The usage windows the provider reports, each self-labelled; empty when it reports none.
/// </param>
public sealed record PluginSessionStatus(
    double? ContextUsedPercent,
    IReadOnlyList<PluginRateLimitWindow> RateLimits)
{
    /// <summary>
    /// Whether there is anything worth showing, so a header can hide the bars until a provider reports usage.
    /// </summary>
    public bool HasAny => ContextUsedPercent is not null || RateLimits.Count > 0;
}
