using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Cockpit.App.Controls;

// Draws its child small without laying it out small (AC-442): a TerminalControl reports the grid its Bounds
// imply and the host forwards that to the pty, so a tile-sized layout would reflow the session for good.
// Scale 1 is the identity, so the host stays in the tree and switching never rebuilds the pane.
// ponytail: live scaling. Measured at 6 streaming panes (MiniatureFrameCostBenchmark) it costs a third of a
// 60fps budget against a 2 Hz snapshot route's 0.42 ms/frame; snapshots if a real rail drops frames.
public sealed class MiniatureHost : Decorator
{
    public static readonly StyledProperty<double> ScaleProperty =
        AvaloniaProperty.Register<MiniatureHost, double>(nameof(Scale), 1.0);

    static MiniatureHost()
    {
        AffectsMeasure<MiniatureHost>(ScaleProperty);
        AffectsArrange<MiniatureHost>(ScaleProperty);
    }

    public MiniatureHost()
    {
        ClipToBounds = true;
    }

    // The factor the child is drawn at. 1 = full size (identity). The rail's working range is 0.15–0.40.
    public double Scale
    {
        get => GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    // A non-positive scale would divide the available size by zero and hand the child an infinite box.
    private double EffectiveScale => Scale is > 0 and <= 1 ? Scale : 1.0;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child)
        {
            return default;
        }

        var scale = EffectiveScale;
        child.Measure(new Size(availableSize.Width / scale, availableSize.Height / scale));
        return new Size(child.DesiredSize.Width * scale, child.DesiredSize.Height * scale);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is not { } child)
        {
            return finalSize;
        }

        var scale = EffectiveScale;
        child.Arrange(new Rect(0, 0, finalSize.Width / scale, finalSize.Height / scale));

        // Only on change: Arrange runs on every layout pass and a fresh transform each time would
        // invalidate the child's render every one of them.
        if (child.RenderTransform is not ScaleTransform current
            || current.ScaleX != scale || current.ScaleY != scale)
        {
            child.RenderTransformOrigin = RelativePoint.TopLeft;
            child.RenderTransform = new ScaleTransform(scale, scale);
        }

        return finalSize;
    }
}
