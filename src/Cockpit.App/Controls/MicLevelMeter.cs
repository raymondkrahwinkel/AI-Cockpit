using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.App.Theming;

namespace Cockpit.App.Controls;

// A thin horizontal level meter for calibrating the barge-in threshold (AC-9): a track, a fill for live
// microphone `Level` (0..1), and a marker line at `Threshold`. The fill turns accent-coloured past the
// threshold, so the operator can see how loud "…louder than X" really is and set it by eye while talking.
public sealed class MicLevelMeter : Control
{
    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<MicLevelMeter, double>(nameof(Level));

    public static readonly StyledProperty<double> ThresholdProperty =
        AvaloniaProperty.Register<MicLevelMeter, double>(nameof(Threshold));

    static MicLevelMeter() => AffectsRender<MicLevelMeter>(LevelProperty, ThresholdProperty);

    public double Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public double Threshold
    {
        get => GetValue(ThresholdProperty);
        set => SetValue(ThresholdProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var radius = height / 2;
        var level = Math.Clamp(Level, 0, 1);
        var threshold = Math.Clamp(Threshold, 0, 1);

        // Cockpit palette resolved from Theme.axaml at render so a future light/alt theme swap is followed: hairline
        // track, "done" green while below the threshold, accent once it would trip barge-in, near-white marker line.
        context.DrawRectangle(ThemeBrush.Resolve("CockpitHairlineBrush", "#2a2f39"), null, new RoundedRect(new Rect(0, 0, width, height), radius));

        var fillWidth = level * width;
        if (fillWidth > 0)
        {
            var fill = level >= threshold ? ThemeBrush.Resolve("CockpitAccentBrush", "#2563eb") : ThemeBrush.Resolve("CockpitStatusDoneBrush", "#5AA576");
            context.DrawRectangle(fill, null, new RoundedRect(new Rect(0, 0, fillWidth, height), radius));
        }

        var markerX = threshold * width;
        context.DrawLine(new Pen(ThemeBrush.Resolve("CockpitTextPrimaryBrush", "#e8eaef"), 1.5), new Point(markerX, 0), new Point(markerX, height));
    }
}
