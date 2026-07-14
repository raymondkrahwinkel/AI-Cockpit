using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Cockpit.App.Controls;

/// <summary>
/// One limit in a session's header: a name, a short bar, and the percentage — <c>ctx ▓░░░░ 5%</c>.
/// <para>
/// A bar rather than a number alone because the number is not what the operator is after: they want to know
/// whether something is running out, and a filled strip answers that without being read. The colour carries the
/// same message twice (a bar that is nearly full is also amber, then red), since a length is hard to judge at
/// four pixels and colour alone is no good to anyone who cannot see it.
/// </para>
/// <para>
/// Hidden entirely when there is nothing to report: Claude says nothing about the rate limits before the first
/// response, and an empty bar reading "0%" would be a claim rather than a silence.
/// </para>
/// </summary>
public sealed class LimitBar : TemplatedControl
{
    /// <summary>Above this, the bar warns. Chosen because a session past two-thirds is one worth noticing, not one worth interrupting.</summary>
    private const double WarnAbove = 60;

    /// <summary>Above this, the bar is red: close enough that the operator should decide something rather than discover it mid-turn.</summary>
    private const double CriticalAbove = 85;

    private const double TrackWidth = 34;

    private const double Gap = 5;

    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<LimitBar, string>(nameof(Label), string.Empty);

    public static readonly StyledProperty<double?> PercentProperty =
        AvaloniaProperty.Register<LimitBar, double?>(nameof(Percent));

    /// <summary>The short name shown before the bar: <c>ctx</c>, <c>5h</c>, <c>wk</c>.</summary>
    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>How much of this limit is used, 0-100 — or null when Claude has not reported it, in which case nothing is drawn.</summary>
    public double? Percent
    {
        get => GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    static LimitBar()
    {
        AffectsRender<LimitBar>(PercentProperty, LabelProperty);
        AffectsMeasure<LimitBar>(PercentProperty, LabelProperty);

        // Nothing to report, nothing to draw: Claude says nothing about the rate limits before the first response,
        // and a bar sitting at zero would be a claim rather than a silence.
        PercentProperty.Changed.AddClassHandler<LimitBar>((bar, args) => bar.IsVisible = args.NewValue is double);
    }

    public LimitBar() => IsVisible = false;

    /// <summary>The width is decided here, in the measure pass — a control that sets its own Width while drawing invites a layout loop.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Percent is not { } percent)
        {
            return default;
        }

        var label = Text(Label, Brushes.Gray);
        var value = Text(Format(percent), Brushes.Gray);

        return new Size(
            label.Width + Gap + TrackWidth + Gap + value.Width,
            Math.Max(label.Height, value.Height));
    }

    public override void Render(DrawingContext context)
    {
        if (Percent is not { } percent)
        {
            return;
        }

        var fill = FillFor(percent);
        var label = Text(Label, Foreground ?? NormalBrush);
        var value = Text(Format(percent), fill);

        var middle = Bounds.Height / 2;
        context.DrawText(label, new Point(0, middle - label.Height / 2));

        var trackLeft = label.Width + Gap;
        context.DrawRectangle(TrackBrush, null, new RoundedRect(new Rect(trackLeft, middle - 2, TrackWidth, 4), 2));

        // Never a sliver of nothing: a limit that has been touched at all draws at least a visible tip, or a 1%
        // context window looks exactly like an untouched one.
        if (percent > 0)
        {
            var filled = Math.Max(2, TrackWidth * Math.Clamp(percent, 0, 100) / 100);
            context.DrawRectangle(fill, null, new RoundedRect(new Rect(trackLeft, middle - 2, filled, 4), 2));
        }

        context.DrawText(value, new Point(trackLeft + TrackWidth + Gap, middle - value.Height / 2));
    }

    private static string Format(double percent) => $"{Math.Round(percent, MidpointRounding.AwayFromZero)}%";

    private FormattedText Text(string text, IBrush brush) =>
        new(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily), FontSize, brush);

    private IBrush FillFor(double percent) =>
        percent >= CriticalAbove ? CriticalBrush
        : percent >= WarnAbove ? WarnBrush
        : NormalBrush;

    // Resolved from the theme so a palette change carries: the same tokens the session status dots use.
    private IBrush TrackBrush => Brush("CockpitHairlineBrush", Brushes.DimGray);

    private IBrush NormalBrush => Brush("CockpitTextSecondaryBrush", Brushes.Gray);

    private IBrush WarnBrush => Brush("CockpitStatusWaitingBrush", Brushes.Orange);

    private IBrush CriticalBrush => Brush("CockpitStatusErrorBrush", Brushes.Red);

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryGetResource(key, ActualThemeVariant, out var resource) && resource is IBrush brush
            ? brush
            : fallback;
}
