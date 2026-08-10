using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Cockpit.App.Controls;

// Draws its child small without laying it out small (AC-442): a TerminalControl reports the grid its Bounds
// imply and the host forwards that to the pty, so a tile-sized layout would reflow the session for good.
// The child is laid out in the box it would have in the focus slot and only *drawn* scaled, so switching is
// a number and never rebuilds the pane.
//
// The box is derived, not given (AC-670). It used to be `availableSize / Scale`, which is only the focus box
// when the host's own box is exactly the focus pane's × the scale — and it never is: between the container the
// rail arranges and this host sits fixed chrome (`PaneRoot`'s 4px margin and 1px border, 10px in all). Dividing
// after that inset has been taken off multiplies it by 1/scale: at 0.31 the child came out ~22px narrower than
// in focus, which is three terminal columns, so a promoted session reflowed. `TileSize` and `FocusSize` are the
// container's box in each of the two states, and the inset between them is measurable here — it is whatever
// this host did not get of the box the panel handed its container.
//
// ponytail: live scaling. Measured at 6 streaming panes (MiniatureFrameCostBenchmark) it costs a third of a
// 60fps budget against a 2 Hz snapshot route's 0.42 ms/frame; snapshots if a real rail drops frames.
public sealed class MiniatureHost : Decorator
{
    // The box the rail gave this host's container. Empty (the default) means "not in a rail" — the child is
    // then laid out at the size it is given, at scale 1.
    public static readonly StyledProperty<Size> TileSizeProperty =
        AvaloniaProperty.Register<MiniatureHost, Size>(nameof(TileSize));

    // The box that same container has in the focus slot: the box the child must keep being laid out in.
    public static readonly StyledProperty<Size> FocusSizeProperty =
        AvaloniaProperty.Register<MiniatureHost, Size>(nameof(FocusSize));

    static MiniatureHost()
    {
        AffectsMeasure<MiniatureHost>(TileSizeProperty, FocusSizeProperty);
        AffectsArrange<MiniatureHost>(TileSizeProperty, FocusSizeProperty);
    }

    public MiniatureHost()
    {
        ClipToBounds = true;
    }

    public Size TileSize
    {
        get => GetValue(TileSizeProperty);
        set => SetValue(TileSizeProperty, value);
    }

    public Size FocusSize
    {
        get => GetValue(FocusSizeProperty);
        set => SetValue(FocusSizeProperty, value);
    }

    // What the child is laid out in, and what it is drawn at, for a host box of `available`. Pure so the
    // arithmetic that the pty depends on is testable without a terminal.
    //
    // `inset` is the chrome between the container and this host — the same markup in both states, so the box
    // the child needs is the focus container minus that same inset. The scale then follows from the width:
    // the tile mirrors the focus pane's shape, so width and height agree to within a pixel or two of the
    // panel's own rounding, and width is the one that decides the column count.
    internal static (Size ChildBox, double Scale) Fit(Size available, Size tile, Size focus)
    {
        if (focus.Width <= 0 || focus.Height <= 0
            || !double.IsFinite(available.Width) || !double.IsFinite(available.Height)
            || available.Width <= 0 || available.Height <= 0)
        {
            return (available, 1.0);
        }

        var inset = new Size(
            Math.Max(0, tile.Width - available.Width),
            Math.Max(0, tile.Height - available.Height));
        var childBox = new Size(focus.Width - inset.Width, focus.Height - inset.Height);

        if (childBox.Width <= 0 || childBox.Height <= 0)
        {
            return (available, 1.0);
        }

        // Above 1 would blow the child up rather than shrink it; that is not a miniature, it is a bug upstream.
        var scale = Math.Min(1.0, available.Width / childBox.Width);
        return scale > 0 ? (childBox, scale) : (available, 1.0);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child)
        {
            return default;
        }

        var (childBox, scale) = Fit(availableSize, TileSize, FocusSize);
        child.Measure(childBox);
        return new Size(child.DesiredSize.Width * scale, child.DesiredSize.Height * scale);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is not { } child)
        {
            return finalSize;
        }

        var (childBox, scale) = Fit(finalSize, TileSize, FocusSize);
        child.Arrange(new Rect(default, childBox));

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
