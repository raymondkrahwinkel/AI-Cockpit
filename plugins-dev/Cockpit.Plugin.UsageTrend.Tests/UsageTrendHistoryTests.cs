using FluentAssertions;

namespace Cockpit.Plugin.UsageTrend.Tests;

/// <summary>
/// The two rules that keep the usage-trend history out of <c>cockpit.json</c>'s way (AC-54): a reading is written
/// at most once per ten minutes per profile unless it jumped, and nothing older than fourteen days is kept. These
/// run on plain lists — the reason the rules live in <see cref="UsageTrendHistory"/> apart from the widget.
/// </summary>
public class UsageTrendHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);

    private static UsageTrendSample Sample(DateTimeOffset at, double? ctx = 20, string? profile = "Default") =>
        new(at, profile, ctx, FiveHourPercent: 30, WeeklyPercent: 40);

    [Fact]
    public void FirstSampleForAProfile_IsAlwaysRecorded()
    {
        var result = UsageTrendHistory.Append([], Sample(T0));

        result.Should().NotBeNull();
        result!.Should().HaveCount(1);
    }

    [Fact]
    public void ASecondSampleWithinTenMinutes_IsDebouncedAway()
    {
        var existing = new[] { Sample(T0) };

        // Five minutes later, essentially the same figures: not worth a whole-file rewrite.
        var result = UsageTrendHistory.Append(existing, Sample(T0.AddMinutes(5), ctx: 21));

        result.Should().BeNull("a near-identical reading inside the 10-minute window is dropped");
    }

    [Fact]
    public void ASamplePastTheDebounceWindow_IsRecorded()
    {
        var existing = new[] { Sample(T0) };

        var result = UsageTrendHistory.Append(existing, Sample(T0.AddMinutes(11), ctx: 21));

        result.Should().NotBeNull();
        result!.Should().HaveCount(2);
    }

    [Fact]
    public void ASharpJumpInsideTheWindow_IsRecordedAnyway()
    {
        var existing = new[] { Sample(T0, ctx: 20) };

        // Two minutes later but the context jumped 20 -> 80: a scarp the 10-minute grid must not skip.
        var result = UsageTrendHistory.Append(existing, Sample(T0.AddMinutes(2), ctx: 80));

        result.Should().NotBeNull("a jump past the threshold overrides the debounce");
        result!.Should().HaveCount(2);
    }

    [Fact]
    public void AContextResetToNull_CountsAsAJump()
    {
        var existing = new[] { Sample(T0, ctx: 60) };

        // A /compact drops the context to "not reported": presence changing is itself a jump worth a point.
        var result = UsageTrendHistory.Append(existing, Sample(T0.AddMinutes(1), ctx: null));

        result.Should().NotBeNull();
        result!.Should().HaveCount(2);
    }

    [Fact]
    public void DebounceIsPerProfile_ASecondProfilesFirstPointIsNotGated()
    {
        var existing = new[] { Sample(T0, profile: "Work") };

        // A different profile, one minute later: its first point must not be held back by Work's debounce window.
        var result = UsageTrendHistory.Append(existing, Sample(T0.AddMinutes(1), profile: "Personal"));

        result.Should().NotBeNull();
        result!.Should().HaveCount(2);
    }

    [Fact]
    public void ASampleWithNoUsageFigures_IsNeverRecorded()
    {
        var candidate = new UsageTrendSample(T0, "Default", ContextPercent: null, FiveHourPercent: null, WeeklyPercent: null);

        var result = UsageTrendHistory.Append([], candidate);

        result.Should().BeNull("a row of three nulls is a silence, not a data point");
    }

    [Fact]
    public void SamplesOlderThanFourteenDays_ArePrunedOnAppend()
    {
        var stale = Sample(T0.AddDays(-15));       // beyond retention
        var recent = Sample(T0.AddDays(-1));        // within retention
        var existing = new[] { stale, recent };

        var result = UsageTrendHistory.Append(existing, Sample(T0));

        result.Should().NotBeNull();
        result!.Should().NotContain(stale, "anything past 14 days is dropped in the same write");
        result.Should().Contain(recent);
        result.Should().HaveCount(2, "the recent sample and the new one survive; the 15-day-old one does not");
    }

    [Fact]
    public void Prune_KeepsExactlyTheFourteenDayWindow_InTimeOrder()
    {
        var samples = new[]
        {
            Sample(T0.AddDays(-20)),
            Sample(T0.AddDays(-14).AddMinutes(1)), // just inside
            Sample(T0.AddDays(-2)),
            Sample(T0.AddDays(-14).AddMinutes(-1)), // just outside
        };

        var kept = UsageTrendHistory.Prune(samples, T0);

        kept.Should().HaveCount(2);
        kept.Select(sample => sample.TimestampUtc).Should().BeInAscendingOrder();
        kept.Should().OnlyContain(sample => sample.TimestampUtc >= T0 - TimeSpan.FromDays(14));
    }
}
