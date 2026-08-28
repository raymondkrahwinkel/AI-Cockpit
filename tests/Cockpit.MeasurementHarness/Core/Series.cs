namespace Cockpit.MeasurementHarness.Core;

/// <summary>One point of a series: the variable that was swept, and what came out.</summary>
public sealed record SeriesPoint(double X, double Y);

/// <summary>What the shape test says about a series, for the report header.</summary>
public sealed record ShapeVerdict(bool Holds, string Line, IReadOnlyList<SeriesPoint> Outliers);

/// <summary>
/// E5: a sweep over one variable is one object that tests its own shape, not a stack of separate reports.
/// 7452 rounds at 15 tiles against 3146 at 20 stood in two findings reports for half a day — every run was
/// individually sound, so no instrument check could have caught it. The defect only existed between runs.
/// </summary>
public static class Series
{
    /// <summary>Fewer points than this cannot distinguish a shape from noise, so the test says so instead of guessing.</summary>
    public const int MinimumPoints = 4;

    /// <summary>Reduces repeated readings at every sweep value to their median before judging the sweep's shape.</summary>
    public static IReadOnlyList<SeriesPoint> MedianByX(IReadOnlyList<SeriesPoint> points) =>
        points
            .GroupBy(point => point.X)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var values = group.Select(point => point.Y).OrderBy(value => value).ToList();
                var middle = values.Count / 2;
                var median = values.Count % 2 == 0
                    ? (values[middle - 1] + values[middle]) / 2
                    : values[middle];
                return new SeriesPoint(group.Key, median);
            })
            .ToList();

    /// <summary>
    /// Tests a series against the straight line the theory predicts, and names the points that do not
    /// belong to it. Each point is judged against a fit of the others, so one bad value cannot widen the
    /// spread it is measured against and hide inside it.
    /// </summary>
    public static ShapeVerdict Linear(string variable, IReadOnlyList<SeriesPoint> points, double minR2 = 0.9, double outlierSigma = 3.0)
    {
        if (points.Count < MinimumPoints)
        {
            return new ShapeVerdict(true, $"shape ({variable}): not judged, {points.Count} points is under {MinimumPoints}", []);
        }

        var (slope, intercept) = _Fit(points);
        var r2 = _RSquared(points, slope, intercept);
        var outliers = _LeaveOneOutOutliers(points, outlierSigma, out var worstRatio);

        var holds = r2 >= minR2 && outliers.Count == 0;
        var line = $"shape ({variable}): y = {slope:F1}x + {intercept:F1}, R2 = {r2:F4}"
                   + (outliers.Count == 0
                       ? "  — holds"
                       : $"  — DOES NOT HOLD: {outliers.Count} point(s) off the line, worst at {worstRatio:F1}x the spread of the rest"
                         + $" [{string.Join(", ", outliers.Select(p => $"x={p.X:G} y={p.Y:G}"))}]");

        return new ShapeVerdict(holds, line, outliers);
    }

    /// <summary>The cheap variant: the series only has to keep going the same way.</summary>
    public static ShapeVerdict Monotonic(string variable, IReadOnlyList<SeriesPoint> points, bool increasing = true)
    {
        if (points.Count < 2)
        {
            return new ShapeVerdict(true, $"shape ({variable}): not judged, {points.Count} point(s)", []);
        }

        var ordered = points.OrderBy(p => p.X).ToList();
        var breaks = new List<SeriesPoint>();
        for (var i = 1; i < ordered.Count; i++)
        {
            var rising = ordered[i].Y >= ordered[i - 1].Y;
            if (rising != increasing)
            {
                breaks.Add(ordered[i]);
            }
        }

        var direction = increasing ? "non-decreasing" : "non-increasing";
        var line = breaks.Count == 0
            ? $"shape ({variable}): {direction} — holds"
            : $"shape ({variable}): {direction} — DOES NOT HOLD at [{string.Join(", ", breaks.Select(p => $"x={p.X:G} y={p.Y:G}"))}]";

        return new ShapeVerdict(breaks.Count == 0, line, breaks);
    }

    private static (double Slope, double Intercept) _Fit(IReadOnlyList<SeriesPoint> points)
    {
        var n = points.Count;
        double sumX = 0, sumY = 0, sumXy = 0, sumXx = 0;
        foreach (var p in points)
        {
            sumX += p.X;
            sumY += p.Y;
            sumXy += p.X * p.Y;
            sumXx += p.X * p.X;
        }

        var denominator = (n * sumXx) - (sumX * sumX);
        if (Math.Abs(denominator) < double.Epsilon)
        {
            return (0, sumY / n);
        }

        var slope = ((n * sumXy) - (sumX * sumY)) / denominator;
        return (slope, (sumY - (slope * sumX)) / n);
    }

    private static double _RSquared(IReadOnlyList<SeriesPoint> points, double slope, double intercept)
    {
        var mean = points.Average(p => p.Y);
        var total = points.Sum(p => (p.Y - mean) * (p.Y - mean));
        var residual = points.Sum(p => Math.Pow(p.Y - ((slope * p.X) + intercept), 2));
        return total <= double.Epsilon ? 1.0 : 1.0 - (residual / total);
    }

    /// <summary>
    /// The smallest share of a predicted value that still counts as a real deviation. Without it a series
    /// that fits almost perfectly has a spread near zero, and then ordinary noise reads as an outlier —
    /// which would make the test cry wolf on exactly the clean series it is supposed to pass.
    /// </summary>
    private const double MinimumRelativeDeviation = 0.02;

    private static List<SeriesPoint> _LeaveOneOutOutliers(IReadOnlyList<SeriesPoint> points, double sigma, out double worstRatio)
    {
        var outliers = new List<SeriesPoint>();
        worstRatio = 0;

        for (var i = 0; i < points.Count; i++)
        {
            // Each point is judged against a fit of the others, so a bad value cannot widen the spread it is
            // measured against and then sit comfortably inside it.
            var rest = points.Where((_, index) => index != i).ToList();
            var (slope, intercept) = _Fit(rest);
            var predicted = (slope * points[i].X) + intercept;
            var restResiduals = rest.Select(p => p.Y - ((slope * p.X) + intercept)).ToList();
            var spread = Math.Max(_StandardDeviation(restResiduals), Math.Abs(predicted) * MinimumRelativeDeviation);
            if (spread <= double.Epsilon)
            {
                continue;
            }

            var ratio = Math.Abs(points[i].Y - predicted) / spread;
            if (ratio <= sigma)
            {
                continue;
            }

            outliers.Add(points[i]);
            worstRatio = Math.Max(worstRatio, ratio);
        }

        return outliers;
    }

    private static double _StandardDeviation(IReadOnlyList<double> values)
    {
        if (values.Count < 2)
        {
            return 0;
        }

        var mean = values.Average();
        return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1));
    }
}
