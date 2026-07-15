namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// One usage window a provider reports against — how much of a rolling allowance is spent and when it rolls
/// over (#45 D7). Kept provider-neutral on purpose: a window is "used X% of a span that resets at T", not
/// Claude's specific five-hour/weekly pair, so a provider with different windows (Codex reports its own) fits
/// the same shape and the host maps it to whatever slots it renders.
/// </summary>
/// <param name="UsedPercent">How much of the window is spent, 0-100.</param>
/// <param name="ResetsAt">When the window rolls over, or <see langword="null"/> when the provider did not say.</param>
/// <param name="WindowMinutes">The window's span in minutes, letting the host tell a shorter window from a longer one; <see langword="null"/> when the provider did not say.</param>
public sealed record PluginRateLimitWindow(
    double UsedPercent,
    DateTimeOffset? ResetsAt,
    int? WindowMinutes);
