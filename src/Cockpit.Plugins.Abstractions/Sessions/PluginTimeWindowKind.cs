namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// What changes while a <see cref="PluginTimeWindow"/> is open (AC-1808).
/// </summary>
public enum PluginTimeWindowKind
{
    /// <summary>
    /// The provider's limits run out faster or are stricter inside the window; the price stays the same.
    /// </summary>
    PeakLimits,

    /// <summary>
    /// A different price applies inside the window, as the <see cref="PluginPriceModifier"/> carrying it says.
    /// </summary>
    OffPeakPrice,
}
