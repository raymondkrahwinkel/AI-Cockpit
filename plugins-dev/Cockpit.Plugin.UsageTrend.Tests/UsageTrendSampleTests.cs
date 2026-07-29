using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.UsageTrend.Tests;

/// <summary>
/// Flattening a host <see cref="SessionUsageSnapshot"/> into a stored sample (AC-54): the context percentage and
/// profile label carry straight over, and the five-hour / weekly figures are matched off the window labels the
/// provider gives them ("5h" / "wk") rather than their position — so a provider that reports different windows
/// contributes no false 5h/wk line.
/// </summary>
public class UsageTrendSampleTests
{
    private static readonly DateTimeOffset At = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);

    private static PluginRateLimitWindow Window(string label, double percent) => new(label, percent, ResetsAt: null, WindowMinutes: null);

    [Fact]
    public void From_MapsContextProfileAndTheFiveHourAndWeeklyWindowsByLabel()
    {
        var snapshot = new SessionUsageSnapshot(
            "Work",
            ContextUsedPercent: 42,
            RateLimits: [Window("5h", 55), Window("wk", 66)]);

        var sample = UsageTrendSample.From(snapshot, At);

        Assert.Equal(At, sample.TimestampUtc);
        Assert.Equal("Work", sample.ProfileLabel);
        Assert.Equal(42, sample.ContextPercent);
        Assert.Equal(55, sample.FiveHourPercent);
        Assert.Equal(66, sample.WeeklyPercent);
    }

    [Fact]
    public void From_LeavesAWindowNull_WhenTheProviderDoesNotReportThatLabel()
    {
        // Only a five-hour window; no weekly one — the weekly figure must stay null rather than borrow another's.
        var snapshot = new SessionUsageSnapshot("Default", ContextUsedPercent: 10, RateLimits: [Window("5h", 20)]);

        var sample = UsageTrendSample.From(snapshot, At);

        Assert.Equal(20, sample.FiveHourPercent);
        Assert.Null(sample.WeeklyPercent);
    }

    [Fact]
    public void From_MatchesLabelsCaseInsensitively()
    {
        var snapshot = new SessionUsageSnapshot("Default", ContextUsedPercent: null, RateLimits: [Window("5H", 33), Window("WK", 44)]);

        var sample = UsageTrendSample.From(snapshot, At);

        Assert.Equal(33, sample.FiveHourPercent);
        Assert.Equal(44, sample.WeeklyPercent);
    }

    [Fact]
    public void From_MapsAnUnrecognisedWindow_ToNeitherLine()
    {
        // A provider whose only window is a monthly allowance: it must not be misread as five-hourly.
        var snapshot = new SessionUsageSnapshot("Default", ContextUsedPercent: 5, RateLimits: [Window("mo", 90)]);

        var sample = UsageTrendSample.From(snapshot, At);

        Assert.Null(sample.FiveHourPercent);
        Assert.Null(sample.WeeklyPercent);
        Assert.True(sample.HasAny, "the context figure still makes it a real data point");
    }
}
