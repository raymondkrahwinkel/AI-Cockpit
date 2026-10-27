namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// What a <see cref="PluginModelObservation"/> saw (AC-1808).
/// </summary>
public enum PluginModelObservationKind
{
    /// <summary>
    /// The provider reported how far a usage window stands; <see cref="PluginModelObservation.Quota"/> carries it.
    /// </summary>
    QuotaReading,

    /// <summary>
    /// The provider turned a request away because a rate limit or usage limit was reached.
    /// </summary>
    RateLimitRefusal,

    /// <summary>
    /// The provider started answering a request; <see cref="PluginModelObservation.Latency"/> says how long that took.
    /// </summary>
    Latency,
}
