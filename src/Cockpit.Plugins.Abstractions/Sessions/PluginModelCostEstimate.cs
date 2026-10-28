namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// What one of a provider's models costs, as that provider estimates it — the cockpit never works a figure out
/// itself, the same way <see cref="PluginTurnCompleted.TotalCostUsd"/> is reported rather than computed. "Estimated"
/// is in the property names on purpose: a plugin that has no priced feed to read is left carrying a figure it
/// compiled in, and cannot tell when that figure went stale. A consumer showing one has to name it an estimate to
/// read the property at all.
/// </summary>
/// <param name="Model">
/// The model this estimate is about, exactly as it appears in the provider's own choices — the key a consumer
/// matches on, never a display label.
/// </param>
public sealed record PluginModelCostEstimate(string Model)
{
    /// <summary>
    /// Estimated price per million input tokens in USD, or <see langword="null"/> when the provider offers no figure
    /// — most cannot. Null means "unknown", never "free": a provider that genuinely runs free says so with 0.
    /// </summary>
    public decimal? EstimatedInputUsdPerMillionTokens { get; init; }

    /// <summary>
    /// Estimated price per million output tokens in USD, or <see langword="null"/> when the provider offers no figure.
    /// </summary>
    public decimal? EstimatedOutputUsdPerMillionTokens { get; init; }

    /// <summary>
    /// The model's id in public model data, e.g. <c>"anthropic/claude-sonnet-5.5"</c>, so the host can join it to a
    /// public source without an alias table of its own (AC-1808). <see langword="null"/> when the provider cannot say.
    /// </summary>
    public string? PublicModelRef { get; init; }

    /// <summary>
    /// Where the prices came from and how long they hold (AC-1808). <see langword="null"/> on an estimate a provider
    /// compiled in; a consumer then has no age to show and treats the figures as unverified.
    /// </summary>
    public PluginFactSource? Source { get; init; }

    /// <summary>
    /// The factors on these prices the provider reports live (AC-1808). Empty (the default) when it reports none.
    /// </summary>
    public IReadOnlyList<PluginPriceModifier> PriceModifiers { get; init; } = [];
}
