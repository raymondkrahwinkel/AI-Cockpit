using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Cockpit.App.Controls;

// AC-1013: One limit in a session's header (`ctx ▓░░░░ 5%`) — a bar rather than a number alone since a
// filled strip signals "running out" without being read, colour doubling the message for accessibility;
// hidden entirely when nothing to report so an empty "0%" bar isn't a false claim before Claude's first response.
public sealed class LimitBar : TemplatedControl
{
    private const double TrackWidth = 34;

    private const double Gap = 5;

    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<LimitBar, string>(nameof(Label), string.Empty);

    public static readonly StyledProperty<double?> PercentProperty =
        AvaloniaProperty.Register<LimitBar, double?>(nameof(Percent));

    // The short name shown before the bar: `ctx`, `5h`, `wk`.
    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    // How much of this limit is used, 0-100 — or null when Claude has not reported it, in which case nothing is drawn.
    public double? Percent
    {
        get => GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    // How full this figure has to be before it colours, as its provider declared it; null falls back to `UsageSeverity.FallbackThreshold`.
    public static readonly StyledProperty<double?> ThresholdProperty =
        AvaloniaProperty.Register<LimitBar, double?>(nameof(Threshold));

    public double? Threshold
    {
        get => GetValue(ThresholdProperty);
        set => SetValue(ThresholdProperty, value);
    }

    public static readonly StyledProperty<bool> StretchTrackProperty =
        AvaloniaProperty.Register<LimitBar, bool>(nameof(StretchTrack));

    // When true the track fills the control's width and the percentage right-aligns, instead of the fixed 34px
    // track — for the roomy usage flyout (AC-37), where three short bars in a wide panel looked lost. The compact
    // header strip leaves it false.
    public bool StretchTrack
    {
        get => GetValue(StretchTrackProperty);
        set => SetValue(StretchTrackProperty, value);
    }

    static LimitBar()
    {
        AffectsRender<LimitBar>(PercentProperty, LabelProperty, StretchTrackProperty, ThresholdProperty);
        AffectsMeasure<LimitBar>(PercentProperty, LabelProperty, StretchTrackProperty);

        // Nothing to report, nothing to draw: Claude says nothing about the rate limits before the first response,
        // and a bar sitting at zero would be a claim rather than a silence.
        PercentProperty.Changed.AddClassHandler<LimitBar>((bar, args) => bar.IsVisible = args.NewValue is double);
    }

    public LimitBar() => IsVisible = false;

    // The width is decided here, in the measure pass — a control that sets its own Width while drawing invites a layout loop.
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Percent is not { } percent)
        {
            return default;
        }

        // No brush: measuring asks how much room the glyphs need, and nothing here reaches the screen. Naming a
        // colour would be naming one nobody ever sees.
        var label = Text(Label, null);
        var value = Text(Format(percent), null);
        var height = Math.Max(label.Height, value.Height);

        // Stretch mode: take the width the panel offers (finite in the flyout), so the track can fill it. Falls
        // back to the fixed layout when the width is unconstrained (nothing to stretch into).
        if (StretchTrack && !double.IsInfinity(availableSize.Width))
        {
            return new Size(availableSize.Width, height);
        }

        return new Size(label.Width + Gap + TrackWidth + Gap + value.Width, height);
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

        // Stretch mode fills the width and right-aligns the percentage; otherwise the fixed 34px track with the
        // percentage just after it.
        var trackWidth = StretchTrack
            ? Math.Max(TrackWidth, Bounds.Width - label.Width - value.Width - (Gap * 2))
            : TrackWidth;
        var valueLeft = StretchTrack
            ? Bounds.Width - value.Width
            : trackLeft + TrackWidth + Gap;

        context.DrawRectangle(TrackBrush, null, new RoundedRect(new Rect(trackLeft, middle - 2, trackWidth, 4), 2));

        // Never a sliver of nothing: a limit that has been touched at all draws at least a visible tip, or a 1%
        // context window looks exactly like an untouched one.
        if (percent > 0)
        {
            var filled = Math.Max(2, trackWidth * Math.Clamp(percent, 0, 100) / 100);
            context.DrawRectangle(fill, null, new RoundedRect(new Rect(trackLeft, middle - 2, filled, 4), 2));
        }

        context.DrawText(value, new Point(valueLeft, middle - value.Height / 2));
    }

    private static string Format(double percent) => $"{Math.Round(percent, MidpointRounding.AwayFromZero)}%";

    private FormattedText Text(string text, IBrush? brush) =>
        new(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily), FontSize, brush);

    // Amber where this signal's provider said it starts to matter, red halfway from there to full. The threshold
    // travels with the figure rather than living here, so the bar, the pill and the warning cannot disagree.
    private IBrush FillFor(double percent)
    {
        var warnAt = Threshold ?? UsageSeverity.FallbackThreshold;

        return percent >= UsageSeverity.CriticalAt(warnAt) ? CriticalBrush
            : percent >= warnAt ? WarnBrush
            : NormalBrush;
    }

    // Resolved from the theme so a palette change carries: the same tokens the session status dots use.
    private IBrush TrackBrush => Brush("CockpitHairlineBrush", "#2a2f39");

    private IBrush NormalBrush => Brush("CockpitTextSecondaryBrush", "#949aa5");

    private IBrush WarnBrush => Brush("CockpitStatusWaitingBrush", "#E0A33E");

    private IBrush CriticalBrush => Brush("CockpitStatusErrorBrush", "#D64545");

    // AC-1013: Looked up from this control outwards so a hosting panel's palette override is honoured;
    // the fallback hex is only reached with no resources at all (designer/headless test) and is kept equal
    // to its token by the repo's theme guard. Details: dropped the "named framework colour" failure mode.
    private IBrush Brush(string key, string fallbackHex) =>
        this.TryGetResource(key, ActualThemeVariant, out var resource) && resource is IBrush brush
            ? brush
            : new SolidColorBrush(Color.Parse(fallbackHex));
}
