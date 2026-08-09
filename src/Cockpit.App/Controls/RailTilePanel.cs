using Avalonia;
using Avalonia.Controls;

namespace Cockpit.App.Controls;

// Arranges the miniature-session tiles inside the rail (AC-443), using `RailLayoutMath` for every bit of
// the geometry — columns, tile size, and the row count a wrapping `ScrollViewer` scrolls through. Meant
// as an `ItemsControl.ItemsPanel`, one tile per child, the same role `SessionTilePanel` plays for the
// main session grid.
public sealed class RailTilePanel : Panel
{
    private const double Gutter = 8;

    public static readonly StyledProperty<double> MinTileWidthProperty =
        AvaloniaProperty.Register<RailTilePanel, double>(nameof(MinTileWidth), FocusRailPanel.MinRailWidth);

    public static readonly StyledProperty<double> FocusAspectRatioProperty =
        AvaloniaProperty.Register<RailTilePanel, double>(nameof(FocusAspectRatio), 16.0 / 10.0);

    static RailTilePanel()
    {
        AffectsMeasure<RailTilePanel>(MinTileWidthProperty);
        AffectsMeasure<RailTilePanel>(FocusAspectRatioProperty);
    }

    // The narrowest a tile may get before the rail folds to fewer columns.
    public double MinTileWidth
    {
        get => GetValue(MinTileWidthProperty);
        set => SetValue(MinTileWidthProperty, value);
    }

    // The focus pane's own width/height ratio — a tile mirrors its shape, not a fixed one.
    public double FocusAspectRatio
    {
        get => GetValue(FocusAspectRatioProperty);
        set => SetValue(FocusAspectRatioProperty, value);
    }

    // The geometry from the last measure pass, so the host driving each tile's `MiniatureHost.Scale`
    // (tile width / the focus pane's actual width) can read it without recomputing it.
    internal RailLayoutMath.Geometry Geometry { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var geometry = RailLayoutMath.Compute(availableSize.Width, availableSize.Height, Children.Count, MinTileWidth, FocusAspectRatio, Gutter);
        Geometry = geometry;

        var tileSize = new Size(geometry.TileWidth, geometry.TileHeight);
        foreach (var child in Children)
        {
            child.Measure(tileSize);
        }

        return new Size(availableSize.Width, geometry.ContentHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var geometry = Geometry;
        var tileSize = new Size(geometry.TileWidth, geometry.TileHeight);
        for (var i = 0; i < Children.Count; i++)
        {
            var (x, y) = RailLayoutMath.TileOrigin(i, geometry, Gutter);
            Children[i].Arrange(new Rect(new Point(x, y), tileSize));
        }

        return finalSize;
    }
}
