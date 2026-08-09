using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Cockpit.App.Controls;

// Draws its child small without laying it out small (AC-442). A pane's TerminalControl derives its
// column/row grid from its own Bounds and forwards that to the pty, so a tile-sized *layout* would put the
// session on ~30 columns and reflow the agent's output for good. This measures and arranges the child at
// full size — Bounds, and with them the grid, stay exactly what they were — and scales only the rendering.
// Scale 1 is the identity: the host is always in the tree, so switching between full size and miniature
// changes one number and never rebuilds the pane (a rebuilt view stranded a relaunched TTY on
// "Launching TUI...", see CockpitView.axaml's session-grid comment).
//
// ponytail: live scaling, not snapshots. Measured (MiniatureFrameCostBenchmark, 6 streaming panes): 5.8–6.4
// ms/frame against 0.42 for a 2 Hz snapshot route — 14x, but still a third of a 60fps budget, and only while
// all six stream at once. Snapshots buy that back for a bitmap cache, a cadence and staleness. Take that
// route if a real rail drops frames.
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
