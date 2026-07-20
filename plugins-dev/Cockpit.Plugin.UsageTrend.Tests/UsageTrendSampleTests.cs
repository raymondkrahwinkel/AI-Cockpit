using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;

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

        sample.TimestampUtc.Should().Be(At);
        sample.ProfileLabel.Should().Be("Work");
        sample.ContextPercent.Should().Be(42);
        sample.FiveHourPercent.Should().Be(55);
        sample.WeeklyPercent.Should().Be(66);
    }

    [Fact]
    public void From_LeavesAWindowNull_WhenTheProviderDoesNotReportThatLabel()
    {
        // Only a five-hour window; no weekly one — the weekly figure must stay null rather than borrow another's.
        var snapshot = new SessionUsageSnapshot("Default", ContextUsedPercent: 10, RateLimits: [Window("5h", 20)]);

        var sample = UsageTrendSample.From(snapshot, At);

        sample.FiveHourPercent.Should().Be(20);
        sample.WeeklyPercent.Should().BeNull();
    }

    [Fact]
    public void From_MatchesLabelsCaseInsensitively()
    {
        var snapshot = new SessionUsageSnapshot("Default", ContextUsedPercent: null, RateLimits: [Window("5H", 33), Window("WK", 44)]);

        var sample = UsageTrendSample.From(snapshot, At);

        sample.FiveHourPercent.Should().Be(33);
        sample.WeeklyPercent.Should().Be(44);
    }

    [Fact]
    public void From_MapsAnUnrecognisedWindow_ToNeitherLine()
    {
        // A provider whose only window is a monthly allowance: it must not be misread as five-hourly.
        var snapshot = new SessionUsageSnapshot("Default", ContextUsedPercent: 5, RateLimits: [Window("mo", 90)]);

        var sample = UsageTrendSample.From(snapshot, At);

        sample.FiveHourPercent.Should().BeNull();
        sample.WeeklyPercent.Should().BeNull();
        sample.HasAny.Should().BeTrue("the context figure still makes it a real data point");
    }
}
