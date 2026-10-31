namespace Cockpit.Core.ModelIntelligence;

public enum ModelPatternMetric
{
    QuotaDrainPercentPerHour,
    LimitHitShare,
    MedianLatencySeconds,
}

// A stretch of local hours on some weekdays where the metric stands above the rest; End 00:00 means midnight.
public sealed record PatternWindow(IReadOnlyList<DayOfWeek> Days, TimeOnly Start, TimeOnly End, double Value, double Baseline, int N);

// One metric for one provider/model, with how much it rests on. Peaks may be empty: enough data, no pressure standing out.
public sealed record DerivedPattern(
    string Provider,
    string? Model,
    ModelPatternMetric Metric,
    string TimeZoneId,
    IReadOnlyList<PatternWindow> Peaks,
    int N,
    double Confidence,
    DateTimeOffset From,
    DateTimeOffset To,
    string? Profile = null);

// AC-1812: patterns from our own measurements. No window or threshold is fixed: hour-of-week buckets with too few samples
// are left out, the rest are split into high and low by the best two-group split of their own values, and a metric with
// fewer than two usable buckets yields nothing ("unknown").
public static class ModelPatterns
{
    public static IReadOnlyList<DerivedPattern> Derive(IReadOnlyList<ModelObservation> observations, TimeZoneInfo zone, int minimumSamples = ModelObservationDefaults.MinimumSamples)
    {
        var result = new List<DerivedPattern>();
        foreach (var group in observations.GroupBy(observation => (observation.Provider, observation.Model)))
        {
            var ordered = group.OrderBy(observation => observation.At).ToArray();
            foreach (var metric in Enum.GetValues<ModelPatternMetric>())
            {
                // AC-1982: quota is per profile (rows without one do not count); refusals and latency stay per provider/model.
                var scopes = metric == ModelPatternMetric.QuotaDrainPercentPerHour
                    ? ordered.Where(observation => observation.Kind == ModelObservationKind.QuotaReading && observation.Profile is not null).GroupBy(observation => observation.Profile).Select(profile => (profile.Key, Rows: profile.ToArray()))
                    : [((string?)null, ordered)];
                foreach (var (profile, rows) in scopes)
                {
                    if (_Derive(group.Key.Provider, group.Key.Model, metric, _Points(rows, metric), zone, minimumSamples, profile) is { } pattern)
                    {
                        result.Add(pattern);
                    }
                }
            }
        }

        return result;
    }

    private static List<(DateTimeOffset At, double Value)> _Points(ModelObservation[] ordered, ModelPatternMetric metric)
    {
        switch (metric)
        {
            case ModelPatternMetric.MedianLatencySeconds:
                return [.. ordered.Where(observation => observation.Kind == ModelObservationKind.Latency).Select(observation => (observation.At, observation.Value))];
            case ModelPatternMetric.LimitHitShare:
                // Every refusal is a request that hit, every latency one that did not.
                return [.. ordered.Where(observation => observation.Kind != ModelObservationKind.QuotaReading).Select(observation => (observation.At, observation.Kind == ModelObservationKind.RateLimitRefusal ? 1d : 0d))];
            default:
                var points = new List<(DateTimeOffset, double)>();
                foreach (var window in ordered.Where(observation => observation.Kind == ModelObservationKind.QuotaReading).GroupBy(observation => observation.Window))
                {
                    var readings = window.ToArray();
                    for (var index = 1; index < readings.Length; index++)
                    {
                        var hours = (readings[index].At - readings[index - 1].At).TotalHours;
                        var rise = readings[index].Value - readings[index - 1].Value;

                        // A fall is the window rolling over, not a drain.
                        if (hours > 0 && rise >= 0)
                        {
                            points.Add((readings[index].At, rise / hours));
                        }
                    }
                }

                return points;
        }
    }

    private static DerivedPattern? _Derive(string provider, string? model, ModelPatternMetric metric, List<(DateTimeOffset At, double Value)> points, TimeZoneInfo zone, int minimumSamples, string? profile)
    {
        var buckets = points
            .GroupBy(point => { var local = TimeZoneInfo.ConvertTime(point.At, zone); return (local.DayOfWeek, local.Hour); })
            .Where(bucket => bucket.Count() >= minimumSamples)
            .Select(bucket => (bucket.Key.DayOfWeek, bucket.Key.Hour, N: bucket.Count(), Value: metric == ModelPatternMetric.LimitHitShare ? bucket.Average(point => point.Value) : _Median(bucket.Select(point => point.Value))))
            .ToArray();
        if (buckets.Length < 2)
        {
            return null;
        }

        var used = buckets.Sum(bucket => bucket.N);
        var (threshold, explained) = _BestSplit([.. buckets.Select(bucket => bucket.Value)]);
        var hot = buckets.Where(bucket => bucket.Value > threshold).ToArray();
        var baseline = buckets.Where(bucket => bucket.Value <= threshold).Select(bucket => bucket.Value).DefaultIfEmpty(0).Average();

        var peaks = new List<PatternWindow>();
        var runs = hot.GroupBy(bucket => bucket.DayOfWeek).SelectMany(day => _Runs(day.Key, [.. day.OrderBy(bucket => bucket.Hour)]));
        foreach (var run in runs.GroupBy(run => (run.Start, run.End)))
        {
            var members = hot.Where(bucket => run.Any(r => r.Day == bucket.DayOfWeek) && bucket.Hour >= run.Key.Start && bucket.Hour < run.Key.End).ToArray();
            peaks.Add(new PatternWindow(
                [.. run.Select(r => r.Day).OrderBy(day => ((int)day + 6) % 7)],
                new TimeOnly(run.Key.Start, 0),
                new TimeOnly(run.Key.End % 24, 0),
                members.Average(bucket => bucket.Value),
                baseline,
                members.Sum(bucket => bucket.N)));
        }

        // Shrinks towards 0 while the data is thin, and by how cleanly high and low separate.
        var confidence = used / (double)(used + minimumSamples) * explained;
        return new DerivedPattern(provider, model, metric, zone.Id, peaks, used, confidence, points.Min(point => point.At), points.Max(point => point.At), profile);
    }

    private static IEnumerable<(DayOfWeek Day, int Start, int End)> _Runs(DayOfWeek day, (DayOfWeek DayOfWeek, int Hour, int N, double Value)[] hours)
    {
        var start = hours[0].Hour;
        for (var index = 1; index <= hours.Length; index++)
        {
            if (index == hours.Length || hours[index].Hour != hours[index - 1].Hour + 1)
            {
                yield return (day, start, hours[index - 1].Hour + 1);
                if (index < hours.Length)
                {
                    start = hours[index].Hour;
                }
            }
        }
    }

    // The cut that leaves the least spread inside the two groups; explained is how much of the total spread that removes (0..1).
    private static (double Threshold, double Explained) _BestSplit(double[] values)
    {
        var sorted = values.OrderBy(value => value).ToArray();
        double Spread(ArraySegment<double> part) => part.Count == 0 ? 0 : part.Sum(value => Math.Pow(value - part.Average(), 2));
        var total = Spread(sorted);
        var best = (Threshold: sorted[^1], Spread: total);
        for (var cut = 1; cut < sorted.Length; cut++)
        {
            var spread = Spread(new ArraySegment<double>(sorted, 0, cut)) + Spread(new ArraySegment<double>(sorted, cut, sorted.Length - cut));
            if (spread < best.Spread)
            {
                best = (sorted[cut - 1], spread);
            }
        }

        return (best.Threshold, total == 0 ? 0 : 1 - best.Spread / total);
    }

    private static double _Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(value => value).ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }
}
