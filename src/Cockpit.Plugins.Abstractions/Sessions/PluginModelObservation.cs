namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// One thing a provider saw happen, handed to <see cref="ICockpitHost.RecordModelObservation"/> (AC-1808) so the host
/// can learn over time what no feed publishes — when a limit bites harder, at which hours a model is slow.
/// </summary>
/// <param name="ProviderId">
/// The <see cref="SessionProviderRegistration.ProviderId"/> the observation is about.
/// </param>
/// <param name="Kind">
/// What was seen.
/// </param>
/// <param name="At">
/// When it happened — not when it was handed over.
/// </param>
public sealed record PluginModelObservation(string ProviderId, PluginModelObservationKind Kind, DateTimeOffset At)
{
    /// <summary>
    /// The model involved, as it appears in the provider's own choices; <see langword="null"/> when the provider cannot tell.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// The window as read, for <see cref="PluginModelObservationKind.QuotaReading"/>; <see langword="null"/> otherwise.
    /// </summary>
    public PluginRateLimitWindow? Quota { get; init; }

    /// <summary>
    /// From sending a request to the first output, for <see cref="PluginModelObservationKind.Latency"/>;
    /// <see langword="null"/> otherwise.
    /// </summary>
    public TimeSpan? Latency { get; init; }

    /// <summary>
    /// Which of the provider's accounts the observation is about (AC-1982): an opaque key the provider derives from the
    /// profile's config, so two subscriptions under one provider never read as one. <see langword="null"/> when the
    /// provider cannot tell.
    /// </summary>
    public string? Profile { get; init; }
}
