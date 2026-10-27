namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// A recurring stretch of the week in which a provider behaves differently (AC-1808) — stricter limits at peak hours,
/// or a cheaper price off-peak. The times are wall-clock times in <see cref="TimeZoneId"/>, so a window stays put across
/// daylight-saving changes in its own zone and moves for a reader in a zone that changes on another date.
/// </summary>
/// <param name="Kind">
/// What changes while the window is open.
/// </param>
/// <param name="Start">
/// When the window opens, inclusive.
/// </param>
/// <param name="End">
/// When it closes, exclusive. An <paramref name="End"/> at or before <paramref name="Start"/> runs past midnight into
/// the next day, so 16:00-00:00 is the evening and 22:00-06:00 the night.
/// </param>
/// <param name="TimeZoneId">
/// The IANA zone <paramref name="Start"/> and <paramref name="End"/> are in, e.g. <c>"America/Los_Angeles"</c> or
/// <c>"Etc/UTC"</c> — never an offset, which would be wrong for half the year.
/// </param>
/// <param name="Source">
/// Where the window came from and how long it holds.
/// </param>
public sealed record PluginTimeWindow(
    PluginTimeWindowKind Kind,
    TimeOnly Start,
    TimeOnly End,
    string TimeZoneId,
    PluginFactSource Source)
{
    /// <summary>
    /// The days the window opens on, in <see cref="TimeZoneId"/>; a window past midnight belongs to the day it opens.
    /// Empty (the default) means every day.
    /// </summary>
    public IReadOnlyList<DayOfWeek> Days { get; init; } = [];
}
