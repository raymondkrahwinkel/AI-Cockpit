namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// One usage window a provider reports against — how much of a rolling allowance is spent, when it rolls over,
/// and the <see cref="Label"/> the provider wants shown for it (#45 D7). The provider owns the label so the host
/// bakes in no window vocabulary of its own: a provider whose limits are a five-hour and a weekly window labels
/// them "5h"/"wk", one with different spans labels them differently, and the header renders whatever it is told.
/// </summary>
/// <param name="Label">The short text the header's bar shows for this window (e.g. "5h", "wk"), chosen by the provider.</param>
/// <param name="UsedPercent">How much of the window is spent, 0-100.</param>
/// <param name="ResetsAt">When the window rolls over, or <see langword="null"/> when the provider did not say.</param>
/// <param name="WindowMinutes">The window's span in minutes, when the provider reports one; <see langword="null"/> otherwise.</param>
public sealed record PluginRateLimitWindow(
    string Label,
    double UsedPercent,
    DateTimeOffset? ResetsAt,
    int? WindowMinutes);
