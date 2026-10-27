using Cockpit.Core.ModelIntelligence;

namespace Cockpit.Core.Tests.ModelIntelligence;

// AC-1812: patterns come from our own observations, with nothing fixed but the retention and the minimum sample size.
public sealed class ModelPatternsTests
{
    private static readonly TimeZoneInfo Local = TimeZoneInfo.CreateCustomTimeZone("local+2", TimeSpan.FromHours(2), "local+2", "local+2");

    // Four weeks of requests; on weekdays 14-20 local two of three are refused, otherwise none.
    private static List<ModelObservation> _Series()
    {
        var start = new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.FromHours(2));
        var series = new List<ModelObservation>();
        for (var hour = 0; hour < 4 * 7 * 24; hour++)
        {
            var at = start.AddHours(hour);
            var pressure = at.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday && at.Hour is >= 14 and < 20;
            series.Add(new ModelObservation("claude", "m", ModelObservationKind.Latency, at, 2));
            series.Add(new ModelObservation("claude", "m", ModelObservationKind.Latency, at.AddMinutes(1), 2));
            series.Add(new ModelObservation("claude", "m", pressure ? ModelObservationKind.RateLimitRefusal : ModelObservationKind.Latency, at.AddMinutes(2), pressure ? 1 : 2));
            series.Add(new ModelObservation("claude", "m", pressure ? ModelObservationKind.RateLimitRefusal : ModelObservationKind.Latency, at.AddMinutes(3), pressure ? 1 : 2));
        }

        return series;
    }

    [Fact]
    public void Derive_BelowTheMinimumSampleSize_DerivesNothing()
    {
        var few = _Series().Take(ModelObservationDefaults.MinimumSamples - 1).ToArray();

        Assert.Empty(ModelPatterns.Derive(few, Local));
    }

    [Fact]
    public void Derive_PressureOnWeekdaysFourteenToTwenty_GivesThatWindowInLocalTimeWithNAndConfidence()
    {
        var pattern = Assert.Single(ModelPatterns.Derive(_Series(), Local), found => found.Metric == ModelPatternMetric.LimitHitShare);

        var peak = Assert.Single(pattern.Peaks);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday], peak.Days);
        Assert.Equal(new TimeOnly(14, 0), peak.Start);
        Assert.Equal(new TimeOnly(20, 0), peak.End);
        Assert.True(peak.Value > peak.Baseline);
        Assert.True(pattern.N > 0 && pattern.Confidence is > 0 and <= 1);
    }

    [Fact]
    public void Read_DropsObservationsPastRetention()
    {
        var root = Path.Combine(Path.GetTempPath(), $"observations-{Guid.NewGuid():N}");
        try
        {
            var now = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
            var store = new ModelObservationStore(root, TimeSpan.FromDays(7), new _Clock(now));
            store.Record(new ModelObservation("claude", "m", ModelObservationKind.Latency, now.AddDays(-8), 1));
            store.Record(new ModelObservation("claude", "m", ModelObservationKind.Latency, now.AddDays(-1), 1));

            Assert.Equal(now.AddDays(-1), Assert.Single(store.Read()).At);
            Assert.Single(new ModelObservationStore(root, TimeSpan.FromDays(7), new _Clock(now)).Read());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class _Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
