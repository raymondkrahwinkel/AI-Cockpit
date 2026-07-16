namespace Cockpit.Core.Sessions;

/// <summary>
/// One usage window a session reports against (#45 D7): how much of a rolling allowance is spent, when it rolls
/// over, and the label to show for it. The label comes from the provider, so the header renders it without
/// knowing what "5h" or "wk" means.
/// </summary>
/// <param name="Label">The short text the header's bar shows for this window (e.g. "5h", "wk").</param>
/// <param name="UsedPercent">How much of the window is spent, 0-100.</param>
/// <param name="ResetsAt">When the window rolls over, or <see langword="null"/> when the provider did not say.</param>
public sealed record SessionRateWindow(string Label, double UsedPercent, DateTimeOffset? ResetsAt);
