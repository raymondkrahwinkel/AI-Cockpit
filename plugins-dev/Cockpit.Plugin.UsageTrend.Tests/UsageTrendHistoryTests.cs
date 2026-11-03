
namespace Cockpit.Plugin.UsageTrend.Tests;

// The two rules that keep the usage-trend history out of `cockpit.json`'s way (AC-54): a reading is written
// at most once per ten minutes per profile unless it jumped, and nothing older than fourteen days is kept. These
// run on plain lists — the reason the rules live in `UsageTrendHistory` apart from the widget.
public class UsageTrendHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);

    private static UsageTrendSample Sample(DateTimeOffset at, double? ctx = 20, string? profile = "Default") =>
        new(at, profile, ctx, FiveHourPercent: 30, WeeklyPercent: 40);

    // Same figures within the window are not worth a rewrite; a sharp jump (20 -> 80) or a /compact to "not reported" is,
    // and the window is per profile, so another profile's first point is not held back by Work's.
    [Theory]
    [InlineData(20.0, "Default", 5, 21.0, "Default", false)]
    [InlineData(20.0, "Default", 11, 21.0, "Default", true)]
    [InlineData(20.0, "Default", 2, 80.0, "Default", true)]
    [InlineData(60.0, "Default", 1, null, "Default", true)]
    [InlineData(20.0, "Work", 1, 20.0, "Personal", true)]
    public void ASampleInsideTheTenMinuteWindow_IsDebouncedAway_UnlessItJumpsOrIsAnotherProfile(
        double existingCtx, string existingProfile, int minutesLater, double? ctx, string profile, bool recorded)
    {
        var existing = new[] { Sample(T0, existingCtx, existingProfile) };

        var result = UsageTrendHistory.Append(existing, Sample(T0.AddMinutes(minutesLater), ctx, profile));

        Assert.Equal(recorded ? 2 : (int?)null, result?.Count);
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
