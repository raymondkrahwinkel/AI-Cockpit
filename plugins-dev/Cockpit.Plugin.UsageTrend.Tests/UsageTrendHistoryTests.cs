
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
    public void ASecondSampleWithinTenMinutes_IsDebouncedAway()
    {
        var existing = new[] { Sample(T0) };

        // Five minutes later, essentially the same figures: not worth a whole-file rewrite.
        var result = UsageTrendHistory.Append(existing, Sample(T0.AddMinutes(5), ctx: 21));

        Assert.Null(result);
    }

    [Fact]
    public void ASamplePastTheDebounceWindow_IsRecorded()
    {
        var existing = new[] { Sample(T0) };

        var result = UsageTrendHistory.Append(existing, Sample(T0.AddMinutes(11), ctx: 21));

        Assert.NotNull(result);
        Assert.Equal(2, System.Linq.Enumerable.Count(result!));
    }

    [Fact]
    public void ASharpJumpInsideTheWindow_IsRecordedAnyway()
    {
        var existing = new[] { Sample(T0, ctx: 20) };

        // Two minutes later but the context jumped 20 -> 80: a scarp the 10-minute grid must not skip.
        var result = UsageTrendHistory.Append(existing, Sample(T0.AddMinutes(2), ctx: 80));

        Assert.NotNull(result);
        Assert.Equal(2, System.Linq.Enumerable.Count(result!));
    }

    [Fact]
    public void AContextResetToNull_CountsAsAJump()
    {
        var existing = new[] { Sample(T0, ctx: 60) };

        // A /compact drops the context to "not reported": presence changing is itself a jump worth a point.
        var result = UsageTrendHistory.Append(existing, Sample(T0.AddMinutes(1), ctx: null));

        Assert.NotNull(result);
        Assert.Equal(2, System.Linq.Enumerable.Count(result!));
    }

    [Fact]
    public void DebounceIsPerProfile_ASecondProfilesFirstPointIsNotGated()
    {
        var existing = new[] { Sample(T0, profile: "Work") };

        // A different profile, one minute later: its first point must not be held back by Work's debounce window.
        var result = UsageTrendHistory.Append(existing, Sample(T0.AddMinutes(1), profile: "Personal"));

        Assert.NotNull(result);
        Assert.Equal(2, System.Linq.Enumerable.Count(result!));
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
