namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// A start default a session provider wants the New-session dialog to ask about — Codex has a sandbox and a
/// model. The SDK-session mirror of <see cref="PluginTtyLaunchOption"/>.
/// </summary>
/// <remarks>
/// The provider declares them on its <see cref="SessionProviderRegistration.Options"/>; the host renders them and
/// hands the answers back to <see cref="IPluginSessionDriver.StartAsync(string?, string?, string?, System.Collections.Generic.IReadOnlyDictionary{string, string}?, System.Threading.CancellationToken)"/>,
/// without ever learning what any of them mean.
/// </remarks>
/// <param name="Key">
/// How the answer comes back in the driver's options map.
/// </param>
/// <param name="Label">
/// What the operator reads.
/// </param>
/// <param name="Choices">
/// The values on offer. Empty means free text.
/// </param>
/// <param name="DefaultValue">
/// Pre-selected, or <see langword="null"/> to leave the option unset (the provider's own default then applies).
/// </param>
public sealed record PluginSessionLaunchOption(
    string Key,
    string Label,
    IReadOnlyList<string> Choices,
    string? DefaultValue = null)
{
    /// <summary>
    /// A friendly label per <see cref="Choices"/> value the operator reads instead of the raw value — how Claude
    /// shows "Ask permissions" for the CLI's <c>default</c> mode, or "Low"/"Medium"/"High" for an effort key.
    /// </summary>
    /// <remarks>
    /// Keyed by value; a value with no entry falls back to showing itself, and the value the driver receives is
    /// always the raw <see cref="Choices"/> entry, never the label. <see langword="null"/> (the default) means every
    /// value renders as itself.
    /// </remarks>
    public IReadOnlyDictionary<string, string>? ChoiceLabels { get; init; }

    /// <summary>
    /// What this option's <see cref="Choices"/> cost, as this provider estimates them, ordered cheapest first.
    /// </summary>
    /// <remarks>
    /// Only a provider knows its own prices; the host neither supplies nor checks these. Empty (the default) means
    /// unranked; entries name choices by value, and a choice with no entry is simply unranked.
    /// </remarks>
    public IReadOnlyList<PluginModelCostEstimate> CostEstimatesCheapestFirst { get; init; } = [];
}
