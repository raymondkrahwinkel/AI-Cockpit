using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Cockpit.Plugin.Workflows.Canvas;

/// <summary>
/// The canvas's dotted background (#69). A grid of dots rather than lines: it tells you where you are while you
/// pan and how far you have zoomed, without drawing a cage around the flow. Rendered as a tiled brush, so it
/// costs one fill however far the canvas stretches.
/// </summary>
internal static class DotGrid
{
    private const double Spacing = 16;
    private const double DotSize = 1.6;

    public static IBrush Brush { get; } = _Build();

    private static IBrush _Build()
    {
        var dot = new GeometryDrawing
        {
            Geometry = new EllipseGeometry(new Rect(0, 0, DotSize, DotSize)),
            Brush = new ImmutableSolidColorBrush(Color.Parse("#33333D")),
        };

        return new DrawingBrush(dot)
        {
            TileMode = TileMode.Tile,
            SourceRect = new RelativeRect(0, 0, Spacing, Spacing, RelativeUnit.Absolute),
            DestinationRect = new RelativeRect(0, 0, Spacing, Spacing, RelativeUnit.Absolute),
            Stretch = Stretch.None,
        };
    }
}
