namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// A provider's live status feed (#45 D7) — how full the context window is and the usage windows it reports,
/// the plugin-facing source the host's session header renders its limit bars from. This is the contract that
/// lets a provider report limits without the host running a Claude-specific statusline relay: a driver polls
/// its provider and exposes the latest snapshot here, and the host adapter maps it to the core's status model.
/// <para>
/// The windows are a self-labelled list rather than a fixed five-hour/weekly pair, so the host bakes in no
/// window vocabulary: a provider reports the windows it has, each carrying its own <see cref="PluginRateLimitWindow.Label"/>,
/// and the header renders them in order. A polled snapshot rather than an event — the host reads the driver's
/// current value rather than the driver pushing it onto the narrow event stream, which keeps limits additive on
/// the driver interface (no new event type, no abstractions break).
/// </para>
/// </summary>
/// <param name="ContextUsedPercent">How full the context window is, 0-100, or <see langword="null"/> before the provider reports it.</param>
/// <param name="RateLimits">The usage windows the provider reports, each self-labelled; empty when it reports none.</param>
public sealed record PluginSessionStatus(
    double? ContextUsedPercent,
    IReadOnlyList<PluginRateLimitWindow> RateLimits)
{
    /// <summary>Whether there is anything worth showing, so a header can hide the bars until a provider reports usage.</summary>
    public bool HasAny => ContextUsedPercent is not null || RateLimits.Count > 0;
}
