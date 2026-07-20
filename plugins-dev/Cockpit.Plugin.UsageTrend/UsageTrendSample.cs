using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.UsageTrend;

/// <summary>
/// One point in the usage history: what a profile's session was spending at one moment — how full its context
/// window was and how much of its 5h / weekly allowance was gone. Every figure is nullable, exactly like the
/// source (<see cref="SessionUsageSnapshot"/>): a provider reports rate limits only after the first response, and
/// the context percentage is silent before the first turn and right after a <c>/compact</c>. A row round-trips
/// through <c>IPluginStorage</c> as one JSON object, and the whole history is a list of them.
/// </summary>
/// <param name="TimestampUtc">When the sample was taken, in UTC — the x-axis of the chart and the key retention prunes on.</param>
/// <param name="ProfileLabel">The profile the session ran under, or <see langword="null"/> when unknown. The history groups on it, one line-set per profile.</param>
/// <param name="ContextPercent">How full the context window was, 0-100, or <see langword="null"/> when not reported.</param>
/// <param name="FiveHourPercent">How much of the five-hour allowance was gone, 0-100, or <see langword="null"/>.</param>
/// <param name="WeeklyPercent">How much of the weekly allowance was gone, 0-100, or <see langword="null"/>.</param>
public sealed record UsageTrendSample(
    DateTimeOffset TimestampUtc,
    string? ProfileLabel,
    double? ContextPercent,
    double? FiveHourPercent,
    double? WeeklyPercent)
{
    /// <summary>The label the five-hour window carries in the session's rate limits (what the header pill shows).</summary>
    private const string FiveHourLabel = "5h";

    /// <summary>The label the weekly window carries.</summary>
    private const string WeeklyLabel = "wk";

    /// <summary>Whether this sample carries any usage figure at all — a row of three nulls is a silence, not a data point.</summary>
    public bool HasAny => ContextPercent is not null || FiveHourPercent is not null || WeeklyPercent is not null;

    /// <summary>
    /// Flattens a host usage snapshot into a stored sample at <paramref name="timestampUtc"/>, matching the
    /// five-hour and weekly windows by the label the provider gives them ("5h" / "wk"). A provider that labels its
    /// windows differently simply contributes no 5h/wk line — the widget shows what it recognises rather than
    /// guessing an allowance is five-hourly from its position.
    /// </summary>
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
