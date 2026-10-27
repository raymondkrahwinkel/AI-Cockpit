namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// A ceiling the provider reports (AC-1808) — requests per minute, a context window, a weekly token budget. How much of
/// a rolling allowance is spent right now is a <see cref="PluginQuotaState"/>, not a limit.
/// </summary>
/// <param name="Label">
/// What the limit is, as the provider names it, e.g. <c>"requests per minute"</c> or <c>"context window"</c>.
/// </param>
/// <param name="Value">
/// The ceiling itself, in <paramref name="Unit"/>.
/// </param>
/// <param name="Unit">
/// What <paramref name="Value"/> counts, e.g. <c>"requests/minute"</c> or <c>"tokens"</c>.
/// </param>
/// <param name="Source">
/// Where the limit came from and how long it holds.
/// </param>
public sealed record PluginLimit(string Label, double Value, string Unit, PluginFactSource Source)
{
    /// <summary>
    /// The model the limit is about, as it appears in the provider's own choices; <see langword="null"/> (the default)
    /// when it holds for the whole account.
    /// </summary>
    public string? Model { get; init; }
}
