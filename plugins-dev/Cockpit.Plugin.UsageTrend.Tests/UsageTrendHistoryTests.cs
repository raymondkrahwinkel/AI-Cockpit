
namespace Cockpit.Plugin.UsageTrend.Tests;

// The two rules that keep the usage-trend history out of `cockpit.json`'s way (AC-54): a reading is written
// at most once per ten minutes per profile unless it jumped, and nothing older than fourteen days is kept. These
// run on plain lists — the reason the rules live in `UsageTrendHistory` apart from the widget.
public class UsageTrendHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);

    private static UsageTrendSample Sample(DateTimeOffset at, double? ctx = 20, string? profile = "Default") =>
        new(at, profile, ctx, FiveHourPercent: 30, WeeklyPercent: 40);

    [Fact]
    public void FirstSampleForAProfile_IsAlwaysRecorded()
    {
        var result = UsageTrendHistory.Append([], Sample(T0));

        Assert.NotNull(result);
        Assert.Single(result!);
    }

    [Fact]
    public void ASampleWithNoUsageFigures_IsNeverRecorded()
    {
        var candidate = new UsageTrendSample(T0, "Default", ContextPercent: null, FiveHourPercent: null, WeeklyPercent: null);

        var result = UsageTrendHistory.Append([], candidate);

        Assert.Null(result);
    }

    [Fact]
    public void SamplesOlderThanFourteenDays_ArePrunedOnAppend()
    {
        var stale = Sample(T0.AddDays(-15));       // beyond retention
        var recent = Sample(T0.AddDays(-1));        // within retention
        var existing = new[] { stale, recent };

        var result = UsageTrendHistory.Append(existing, Sample(T0));

        Assert.NotNull(result);
        Assert.DoesNotContain(stale, result!);
        Assert.Contains(recent, result);
        Assert.Equal(2, System.Linq.Enumerable.Count(result!));
    }
}
