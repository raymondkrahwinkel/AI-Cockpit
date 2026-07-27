using Avalonia;
using Avalonia.Controls;
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

    /// <summary>
    /// A fresh tiled brush, built against the theme as it is now. It was a once-computed static, which meant the
    /// dots kept whatever colour was current the first time the type loaded — and, because the colour was a literal
    /// rather than a token, the grid was the one part of the canvas that never followed the repaint at all.
    /// </summary>
    public static IBrush Build()
    {
        var dot = new GeometryDrawing
        {
            Geometry = new EllipseGeometry(new Rect(0, 0, DotSize, DotSize)),
            Brush = _Brush("CockpitHairlineBrush", "#2a2f39"),
        };

        return new DrawingBrush(dot)
        {
            TileMode = TileMode.Tile,
            SourceRect = new RelativeRect(0, 0, Spacing, Spacing, RelativeUnit.Absolute),
            DestinationRect = new RelativeRect(0, 0, Spacing, Spacing, RelativeUnit.Absolute),
            Stretch = Stretch.None,
        };
    }

    /// <summary>
    /// The host's theme brush, resolved at call time — here the hairline, which is what the rest of the app draws
    /// its quietest lines in. The fallback hex is only reached with no <see cref="Application"/> (designer, headless
    /// test) and is held equal to its token by the repository's theme guard.
    /// </summary>
    private static IBrush _Brush(string key, string fallbackHex) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush
            : new ImmutableSolidColorBrush(Color.Parse(fallbackHex));
}
