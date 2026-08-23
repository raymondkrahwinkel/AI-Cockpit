namespace Cockpit.Core.Sessions;

// One usage window a session reports against (#45 D7), label provider-supplied. AC-229/AC-232:
// `ThresholdPercent` travels with the figure it judges; `null` falls back to `UsageSeverity.FallbackThreshold`.
public sealed record SessionRateWindow(string Label, double UsedPercent, DateTimeOffset? ResetsAt, double? ThresholdPercent = null);
