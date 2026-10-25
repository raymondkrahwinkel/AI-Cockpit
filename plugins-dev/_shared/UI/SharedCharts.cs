using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Styling;

namespace Cockpit.Plugin.Shared.UI;

public sealed record ChartSegment(string Label, double Value, Color Color, bool IsHatched = false);
public sealed record StackedBarDatum(string Label, IReadOnlyList<ChartSegment> Segments);
public sealed record GroupedBarDatum(string Label, double First, double Second);

public abstract class ChartControl : Control
{
    protected const double Padding = 12;

    public string EmptyText { get; set; } = "No data";
    public string? TooltipText { get; set; }

    protected IBrush Brush(string key, IBrush fallback)
    {
        if (TryGetResource(key, ActualThemeVariant, out var value) && value is IBrush brush)
        {
            return brush;
        }

        return ActualThemeVariant == ThemeVariant.Dark && key is "ThemeForegroundBrush" or "ThemeAccentForegroundBrush"
            ? Brushes.White
            : fallback;
    }

    protected void Empty(DrawingContext context, Rect bounds)
    {
        var text = new FormattedText(EmptyText, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 12, Brush("ThemeForegroundLowBrush", Brushes.Gray));
        context.DrawText(text, new Point((bounds.Width - text.Width) / 2, (bounds.Height - text.Height) / 2));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        ToolTip.SetTip(this, TooltipText);
    }

    protected void Hatch(DrawingContext context, Rect rect)
    {
        using var clip = context.PushClip(rect);
        var pen = new Pen(Brush("ThemeForegroundLowBrush", Brushes.Gray), 1);
        for (var offset = -rect.Height; offset < rect.Width; offset += 5)
        {
            context.DrawLine(pen, new Point(rect.X + offset, rect.Bottom), new Point(rect.X + offset + rect.Height, rect.Top));
        }
    }
}

public sealed class StackedBarChartControl : ChartControl
{
    public IReadOnlyList<StackedBarDatum> Items { get; set; } = [];

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Items.Count == 0 || Bounds.Width <= Padding * 2 || Bounds.Height <= Padding * 2)
        {
            Empty(context, Bounds);
            return;
        }
        var max = Items.Max(item => item.Segments.Sum(segment => Math.Max(0, segment.Value)));
        if (max <= 0)
        {
            Empty(context, Bounds);
            return;
        }
        var width = (Bounds.Width - Padding * 2) / Items.Count;
        for (var index = 0; index < Items.Count; index++)
        {
            var y = Bounds.Height - Padding;
            foreach (var segment in Items[index].Segments)
            {
                var height = Math.Max(0, segment.Value) / max * (Bounds.Height - Padding * 2);
                y -= height;
                var rect = new Rect(Padding + index * width + 2, y, Math.Max(1, width - 4), height);
                context.DrawRectangle(new SolidColorBrush(segment.Color), null, rect);
                if (segment.IsHatched) { Hatch(context, rect); }
            }
            var total = Items[index].Segments.Sum(segment => Math.Max(0, segment.Value));
            var totalText = new FormattedText(total.ToString("0.#"), System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10, Brush("ThemeForegroundLowBrush", Brushes.Gray));
            context.DrawText(totalText, new Point(Padding + index * width + 2, Math.Max(0, y - totalText.Height)));
            var label = new FormattedText(Items[index].Label, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, Brush("ThemeForegroundLowBrush", Brushes.Gray));
            context.DrawText(label, new Point(Padding + index * width + 2, Bounds.Height - label.Height));
        }
    }
}

public sealed class GroupedBarChartControl : ChartControl
{
    public IReadOnlyList<GroupedBarDatum> Items { get; set; } = [];
    public Color FirstColor { get; set; } = Color.FromRgb(76, 139, 245);
    public Color SecondColor { get; set; } = Color.FromRgb(61, 190, 139);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Items.Count == 0 || Bounds.Width <= Padding * 2 || Bounds.Height <= Padding * 2)
        {
            Empty(context, Bounds);
            return;
        }
        var max = Items.Max(item => Math.Max(item.First, item.Second));
        if (max <= 0)
        {
            Empty(context, Bounds);
            return;
        }
        var group = (Bounds.Width - Padding * 2) / Items.Count;
        for (var i = 0; i < Items.Count; i++)
        {
            var bar = Math.Max(1, (group - 6) / 2);
            var first = Math.Max(0, Items[i].First) / max * (Bounds.Height - Padding * 2);
            var second = Math.Max(0, Items[i].Second) / max * (Bounds.Height - Padding * 2);
            context.DrawRectangle(new SolidColorBrush(FirstColor), null, new Rect(Padding + i * group + 2, Bounds.Height - Padding - first, bar, first));
            context.DrawRectangle(new SolidColorBrush(SecondColor), null, new Rect(Padding + i * group + 4 + bar, Bounds.Height - Padding - second, bar, second));
            var label = new FormattedText(Items[i].Label, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, Brush("ThemeForegroundLowBrush", Brushes.Gray));
            context.DrawText(label, new Point(Padding + i * group + 2, Bounds.Height - label.Height));
        }
    }
}

public sealed class HorizontalBarChartControl : ChartControl
{
    public IReadOnlyList<ChartSegment> Segments { get; set; } = [];

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var total = Segments.Sum(segment => Math.Max(0, segment.Value));
        if (total <= 0 || Bounds.Width <= Padding * 2 || Bounds.Height <= Padding * 2)
        {
            Empty(context, Bounds);
            return;
        }
        var x = Padding;
        foreach (var segment in Segments)
        {
            var width = Math.Max(0, segment.Value) / total * (Bounds.Width - Padding * 2);
            var rect = new Rect(x, Padding, width, Bounds.Height - Padding * 2);
            context.DrawRectangle(new SolidColorBrush(segment.Color), null, rect);
            if (segment.IsHatched) { Hatch(context, rect); }
            x += width;
        }
    }
}

public sealed class StatTile : ChartControl
{
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var muted = Brush("ThemeForegroundLowBrush", Brushes.Gray);
        var foreground = Brush("ThemeForegroundBrush", Brushes.Black);
        context.DrawRectangle(Brush("ThemeCardBrush", Brushes.Transparent), new Pen(Brush("ThemeBorderBrush", Brushes.Gray), 1), Bounds);
        context.DrawText(new FormattedText(Label, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 12, muted), new Point(Padding, Padding));
        context.DrawText(new FormattedText(Value, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 22, foreground), new Point(Padding, Padding + 18));
        context.DrawText(new FormattedText(Detail, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 12, muted), new Point(Padding, Math.Max(Padding + 46, Bounds.Height - Padding - 12)));
    }
}

public sealed class RangePicker : ChartControl
{
    public IReadOnlyList<string> Ranges { get; set; } = ["Uur", "Dag", "Werkweek", "Week", "Maand", "Eigen bereik"];
    public string SelectedRange { get; set; } = "Dag";
    public event EventHandler? Previous;
    public event EventHandler? Next;
    public event EventHandler<string>? SelectedRangeChanged;

    public RangePicker()
    {
        Focusable = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        if (point.X < 32)
        {
            Previous?.Invoke(this, EventArgs.Empty);
        }
        else if (point.X > Bounds.Width - 32)
        {
            Next?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            var index = Math.Clamp((int)((point.X - 32) / Math.Max(1, (Bounds.Width - 64) / Ranges.Count)), 0, Ranges.Count - 1);
            SelectedRange = Ranges[index];
            SelectedRangeChanged?.Invoke(this, SelectedRange);
            InvalidateVisual();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Left)
        {
            Previous?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            Next?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var x = Padding;
        var brush = Brush("ThemeForegroundLowBrush", Brushes.Gray);
        context.DrawText(new FormattedText("‹", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 12, brush), new Point(x, Padding));
        x += 20;
        foreach (var range in Ranges)
        {
            var text = new FormattedText(range, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 12, brush);
            if (range == SelectedRange)
            {
                context.DrawRectangle(Brush("ThemeAccentBrush", Brushes.DodgerBlue), null, new Rect(x - 5, Padding - 4, text.Width + 10, text.Height + 8));
                text = new FormattedText(range, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 12, Brush("ThemeAccentForegroundBrush", Brushes.White));
            }
            context.DrawText(text, new Point(x, Padding));
            x += text.Width + 14;
        }
        context.DrawText(new FormattedText("›", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 12, brush), new Point(x, Padding));
    }
}
