using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Cockpit.App.Controls;

// AC-442: draws its child small without laying it out small — a TerminalControl reports the grid its Bounds
// imply, so a tile-sized layout would resize the session's pty for good.

// AC-670: the child's box comes from the two container boxes, not from `available / Scale`, because the pane's
// own chrome sits between them and dividing after it comes off multiplies it by 1/scale (three columns).

// ponytail: live scaling. At 6 streaming panes it measured at a third of a 60fps budget against a 2 Hz
// snapshot route; use snapshots if a real rail drops frames.
public sealed class MiniatureHost : Decorator
{
    // The box the rail gave this host's container. Empty (the default) means "not in a rail" — the child is
    // then laid out at the size it is given, at scale 1.
    public static readonly StyledProperty<Size> TileSizeProperty =
        AvaloniaProperty.Register<MiniatureHost, Size>(nameof(TileSize));

    // The box that same container has in the focus slot: the box the child must keep being laid out in.
    public static readonly StyledProperty<Size> FocusSizeProperty =
        AvaloniaProperty.Register<MiniatureHost, Size>(nameof(FocusSize));

    // AC-923: the exact box `SessionTilePanel` read back off the focus pane's own host after a real arrange —
    // when set, this IS the child's box, no reconstruction (see `Fit`'s fallback below and the PR description).
    public static readonly StyledProperty<Size> FocusChildBoxProperty =
        AvaloniaProperty.Register<MiniatureHost, Size>(nameof(FocusChildBox));

    static MiniatureHost()
    {
        AffectsMeasure<MiniatureHost>(TileSizeProperty, FocusSizeProperty, FocusChildBoxProperty);
        AffectsArrange<MiniatureHost>(TileSizeProperty, FocusSizeProperty, FocusChildBoxProperty);
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

    public Size FocusChildBox
    {
        get => GetValue(FocusChildBoxProperty);
        set => SetValue(FocusChildBoxProperty, value);
    }

    // AC-670: `inset` is the chrome between the container and this host, the same markup in both states, so the
    // child's box is the focus container minus that same inset. Scale follows from the width, which is what
    // decides a terminal's column count; pure, so the pty's arithmetic is testable without a terminal.
    internal static (Size ChildBox, double Scale) Fit(Size available, Size tile, Size focus, Size focusChildBox = default)
    {
        if (focus.Width <= 0 || focus.Height <= 0
            || !double.IsFinite(available.Width) || !double.IsFinite(available.Height)
            || available.Width <= 0 || available.Height <= 0)
        {
            return (available, 1.0);
        }

        var childBox = focusChildBox is { Width: > 0, Height: > 0 }
            ? focusChildBox
            : new Size(
                focus.Width - Math.Max(0, tile.Width - available.Width),
                focus.Height - Math.Max(0, tile.Height - available.Height));

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

        var (childBox, scale) = Fit(availableSize, TileSize, FocusSize, FocusChildBox);
        child.Measure(childBox);
        return new Size(child.DesiredSize.Width * scale, child.DesiredSize.Height * scale);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is not { } child)
        {
            return finalSize;
        }

        var (childBox, scale) = Fit(finalSize, TileSize, FocusSize, FocusChildBox);
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
