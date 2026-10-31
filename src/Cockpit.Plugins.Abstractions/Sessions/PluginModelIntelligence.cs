namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// What a provider knows about its own models right now, without a session (AC-1808): prices, the windows they change
/// in, its limits and how far its usage windows stand. The answer of
/// <see cref="SessionProviderRegistration.ReadModelIntelligenceAsync"/>.
/// </summary>
/// <remarks>
/// Every list is empty by default, and empty means "the provider does not know", never "none" or 0; the host then falls
/// back to public sources. Every fact names its own <see cref="PluginFactSource"/>, since one reading mixes a quota read
/// a second ago with a price fetched this morning.
/// </remarks>
public sealed record PluginModelIntelligence
{
    /// <summary>
    /// Whether this profile runs the configured model on the operator's own hardware, so public token prices do not apply.
    /// </summary>
    public bool IsSelfHosted { get; init; }

    /// <summary>
    /// The provider's models with their price, <see cref="PluginModelCostEstimate.PublicModelRef"/> and modifiers.
    /// </summary>
    public IReadOnlyList<PluginModelCostEstimate> Models { get; init; } = [];

    /// <summary>
    /// The provider's peak windows. A window that changes a price travels on its <see cref="PluginPriceModifier"/> instead.
    /// </summary>
    public IReadOnlyList<PluginTimeWindow> Windows { get; init; } = [];

    /// <summary>
    /// The ceilings the provider reports, for the account or per model.
    /// </summary>
    public IReadOnlyList<PluginLimit> Limits { get; init; } = [];

    /// <summary>
    /// How far each usage window stood at its last reading.
    /// </summary>
    public IReadOnlyList<PluginQuotaState> Quota { get; init; } = [];

    /// <summary>
    /// The key this read is about, the same one the provider puts on <see cref="PluginModelObservation.Profile"/> (AC-1982),
    /// so the host can match what it learned over time to this profile. <see langword="null"/> when the provider cannot tell.
    /// </summary>
    public string? Profile { get; init; }
}
