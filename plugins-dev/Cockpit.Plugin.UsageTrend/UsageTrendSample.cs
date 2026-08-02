using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.UsageTrend;

// One point in the usage history: what a profile's session was spending at one moment — how full its context
// window was and how much of its 5h / weekly allowance was gone. Every figure is nullable, exactly like the
// source (`SessionUsageSnapshot`): a provider reports rate limits only after the first response, and
// the context percentage is silent before the first turn and right after a `/compact`. A row round-trips
// through `IPluginStorage` as one JSON object, and the whole history is a list of them.
//
// `TimestampUtc`: When the sample was taken, in UTC — the x-axis of the chart and the key retention prunes on.
// `ProfileLabel`: The profile the session ran under, or `null` when unknown. The history groups on it, one line-set per profile.
// `ContextPercent`: How full the context window was, 0-100, or `null` when not reported.
// `FiveHourPercent`: How much of the five-hour allowance was gone, 0-100, or `null`.
// `WeeklyPercent`: How much of the weekly allowance was gone, 0-100, or `null`.
public sealed record UsageTrendSample(
    DateTimeOffset TimestampUtc,
    string? ProfileLabel,
    double? ContextPercent,
    double? FiveHourPercent,
    double? WeeklyPercent)
{
    // The label the five-hour window carries in the session's rate limits (what the header pill shows).
    private const string FiveHourLabel = "5h";

    // The label the weekly window carries.
    private const string WeeklyLabel = "wk";

    // Whether this sample carries any usage figure at all — a row of three nulls is a silence, not a data point.
    public bool HasAny => ContextPercent is not null || FiveHourPercent is not null || WeeklyPercent is not null;

    // Flattens a host usage snapshot into a stored sample at `timestampUtc`, matching the
    // five-hour and weekly windows by the label the provider gives them ("5h" / "wk"). A provider that labels its
    // windows differently simply contributes no 5h/wk line — the widget shows what it recognises rather than
    // guessing an allowance is five-hourly from its position.
    public static UsageTrendSample From(SessionUsageSnapshot snapshot, DateTimeOffset timestampUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new UsageTrendSample(
            timestampUtc,
            snapshot.ProfileLabel,
            snapshot.ContextUsedPercent,
            _Window(snapshot, FiveHourLabel),
            _Window(snapshot, WeeklyLabel));
    }

    private static double? _Window(SessionUsageSnapshot snapshot, string label) =>
        snapshot.RateLimits.FirstOrDefault(window => string.Equals(window.Label, label, StringComparison.OrdinalIgnoreCase))?.UsedPercent;
}
