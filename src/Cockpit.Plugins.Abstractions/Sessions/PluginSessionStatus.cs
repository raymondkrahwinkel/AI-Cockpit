namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// A provider's live status feed (#45 D7) — how full the context window is and how much of its usage windows
/// are spent, the plugin-facing source the host's session header renders its limit bars from. This is the
/// contract that lets a provider report limits without the host running a Claude-specific statusline relay:
/// a driver polls its provider and exposes the latest snapshot here, and the host adapter maps it to the
/// core's limits model.
/// <para>
/// A polled snapshot rather than an event: the host reads the driver's current value rather than the driver
/// pushing it onto the narrow event stream, which keeps limits additive on the driver interface — no new event
/// type, no abstractions break. Every field is optional — a provider reports what it has, and a header hides what
/// it does not.
/// </para>
/// </summary>
/// <param name="ContextUsedPercent">How full the context window is, 0-100, or <see langword="null"/> before the provider reports it.</param>
/// <param name="PrimaryRateLimit">The provider's shorter/immediate usage window, when it reports one.</param>
/// <param name="SecondaryRateLimit">The provider's longer usage window, when it reports one.</param>
public sealed record PluginSessionStatus(
    double? ContextUsedPercent,
    PluginRateLimitWindow? PrimaryRateLimit = null,
    PluginRateLimitWindow? SecondaryRateLimit = null)
{
    /// <summary>Whether there is anything worth showing, so a header can hide the bars until a provider reports usage.</summary>
    public bool HasAny => ContextUsedPercent is not null || PrimaryRateLimit is not null || SecondaryRateLimit is not null;
}
