namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// A factor on a model's price that applies under a condition (AC-1808) — a batch or flex mode, or an off-peak window.
/// A provider reports one only when it reads it live; a discount it merely remembers is not a modifier.
/// </summary>
/// <param name="Condition">
/// What has to hold for the factor to apply, as the provider names it, e.g. <c>"batch"</c>, <c>"flex"</c> or
/// <c>"off-peak"</c>.
/// </param>
/// <param name="Factor">
/// What both estimated prices of the model are multiplied by while it applies: 0.5 is half price, 1.2 a surcharge.
/// </param>
public sealed record PluginPriceModifier(string Condition, decimal Factor)
{
    /// <summary>
    /// When the condition is a time of day or week, the window it holds in; <see langword="null"/> when it is a mode
    /// the caller chooses, such as batch.
    /// </summary>
    public PluginTimeWindow? Window { get; init; }
}
