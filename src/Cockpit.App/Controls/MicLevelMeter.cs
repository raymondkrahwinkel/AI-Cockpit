using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Cockpit.App.Controls;

/// <summary>
/// A thin horizontal level meter for calibrating the barge-in threshold (AC-9): a track, a fill whose width is the
/// live microphone <see cref="Level"/> (0..1), and a marker line at <see cref="Threshold"/>. The fill turns
/// accent-coloured once the level reaches the threshold, so while talking the operator can see exactly how loud
/// "…when the microphone is louder than X" really is and set it by eye.
/// </summary>
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

    // Cockpit palette (Theme.axaml): hairline track, "done" green while below the threshold, accent once it would
    // trip barge-in, and a near-white marker line at the threshold.
    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.Parse("#2C2F37"));
    private static readonly IBrush QuietBrush = new SolidColorBrush(Color.Parse("#5AA576"));
    private static readonly IBrush LoudBrush = new SolidColorBrush(Color.Parse("#D97757"));
    private static readonly Pen MarkerPen = new(new SolidColorBrush(Color.Parse("#E6E8EC")), 1.5);

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

        context.DrawRectangle(TrackBrush, null, new RoundedRect(new Rect(0, 0, width, height), radius));

        var fillWidth = level * width;
        if (fillWidth > 0)
        {
            context.DrawRectangle(level >= threshold ? LoudBrush : QuietBrush, null, new RoundedRect(new Rect(0, 0, fillWidth, height), radius));
        }

        var markerX = threshold * width;
        context.DrawLine(MarkerPen, new Point(markerX, 0), new Point(markerX, height));
    }
}
