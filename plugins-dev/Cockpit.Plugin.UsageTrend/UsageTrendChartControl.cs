using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Cockpit.Plugin.UsageTrend;

/// <summary>
/// One profile's usage over time, drawn straight onto the framework — three lines (context / 5h / weekly) on a
/// 0-100% vertical scale, time along the horizontal, no chart library (there is no sparkline primitive in the
/// host, so this follows the GitStatus control's precedent of vector-drawing its own). It renders only what it can
/// honestly draw: a metric with fewer than two points contributes no line rather than a lone dot pretending to be
/// a trend, and a control with nothing to plot says so in words instead of showing an empty frame that reads as
/// broken — the same "no claim without data" rule the header keeps.
/// </summary>
internal sealed class UsageTrendChartControl : Control
{
    // Distinct, theme-neutral line colours. The header colours its pills by severity (UsageSeverity), but that type
    // is internal to the host and out of a plugin's reach — so a plugin picks its own palette and names each line in
    // the legend rather than borrowing a meaning (a red line here would falsely read as "critical").
    public static readonly Color ContextColor = Color.FromRgb(0x4C, 0x8B, 0xF5);
    public static readonly Color FiveHourColor = Color.FromRgb(0xE8, 0xA3, 0x3D);
    public static readonly Color WeeklyColor = Color.FromRgb(0x3D, 0xBE, 0x8B);

    private const double Padding = 6;

    private IReadOnlyList<UsageTrendSample> _samples = [];

    /// <summary>The profile's samples this chart draws, oldest-first. Assigning re-draws; the widget prunes and groups before handing them over.</summary>
    public IReadOnlyList<UsageTrendSample> Samples
    {
        get => _samples;
        set
        {
            _samples = value ?? [];
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        // The time span the horizontal axis maps onto. Every metric shares it, so the three lines stay aligned in
        // time even where one has gaps the others do not.
        var (minTicks, maxTicks) = _TimeRange();
        var hasSpan = maxTicks > minTicks;

        var plotWidth = width - (2 * Padding);
        var plotHeight = height - (2 * Padding);

        var drewSomething = false;
        if (hasSpan && plotWidth > 0 && plotHeight > 0)
        {
            drewSomething |= _DrawLine(context, sample => sample.ContextPercent, ContextColor, minTicks, maxTicks, plotWidth, plotHeight);
            drewSomething |= _DrawLine(context, sample => sample.FiveHourPercent, FiveHourColor, minTicks, maxTicks, plotWidth, plotHeight);
            drewSomething |= _DrawLine(context, sample => sample.WeeklyPercent, WeeklyColor, minTicks, maxTicks, plotWidth, plotHeight);
        }

        if (!drewSomething)
        {
            _DrawCollectingText(context, width, height);
        }
    }

    private bool _DrawLine(
        DrawingContext context,
        Func<UsageTrendSample, double?> metric,
        Color color,
        long minTicks,
        long maxTicks,
        double plotWidth,
        double plotHeight)
    {
        var points = new List<Point>();
        foreach (var sample in _samples)
        {
            if (metric(sample) is not { } percent)
            {
                continue;
            }

            var clamped = Math.Clamp(percent, 0, 100);
            var x = Padding + (plotWidth * (sample.TimestampUtc.UtcTicks - minTicks) / (double)(maxTicks - minTicks));
            var y = Padding + (plotHeight * (1 - (clamped / 100.0)));
            points.Add(new Point(x, y));
        }

        // One point is a reading, not a trend — no line for it.
        if (points.Count < 2)
        {
            return false;
        }

        var geometry = new StreamGeometry();
        using (var open = geometry.Open())
        {
            open.BeginFigure(points[0], isFilled: false);
            for (var i = 1; i < points.Count; i++)
            {
                open.LineTo(points[i]);
            }

            open.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, new Pen(new SolidColorBrush(color), 1.5), geometry);
        return true;
    }

    private void _DrawCollectingText(DrawingContext context, double width, double height)
    {
        var text = new FormattedText(
            "Collecting usage…",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            12,
            new SolidColorBrush(Color.FromArgb(0x99, 0x88, 0x88, 0x88)));

        var origin = new Point((width - text.Width) / 2, (height - text.Height) / 2);
        context.DrawText(text, origin);
    }

    private (long Min, long Max) _TimeRange()
    {
        var min = long.MaxValue;
        var max = long.MinValue;
        foreach (var sample in _samples)
        {
            var ticks = sample.TimestampUtc.UtcTicks;
            if (ticks < min)
            {
                min = ticks;
            }

            if (ticks > max)
            {
                max = ticks;
            }
        }

        return _samples.Count == 0 ? (0, 0) : (min, max);
    }
}
