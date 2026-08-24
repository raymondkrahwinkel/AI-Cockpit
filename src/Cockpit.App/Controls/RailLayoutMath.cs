namespace Cockpit.App.Controls;

// AC-443/AC-1013: pure, UI-free geometry for the miniature rail — pixel width/height and a tile count
// into columns, rows, tile size and visible-tile count before a scrollbar kicks in. Width alone drives
// column count, height alone drives row count; kept out of `ArrangeOverride` for unit-testability.
internal static class RailLayoutMath
{
    // `TileWidth`/`TileHeight` share the focus pane's aspect ratio — a tile is a scaled mirror of it, not
    // a fixed shape. `ContentHeight` is every row stacked with gutters, for a ScrollViewer's extent.
    // `VisibleRows`/`VisibleCount` are how much fits before `Overflows` says a scrollbar is needed.
    public readonly record struct Geometry(
        int Columns,
        int Rows,
        double TileWidth,
        double TileHeight,
        double ContentHeight,
        int VisibleRows,
        int VisibleCount,
        bool Overflows);

    // `minTileWidth` is the narrowest a tile may get before the rail folds to fewer columns (rail width
    // >= 2x minTileWidth -> two columns, and so on). Columns are capped at `tileCount` so a handful of
    // sessions in a wide rail don't spread into empty columns.
    public static Geometry Compute(
        double railWidth,
        double railHeight,
        int tileCount,
        double minTileWidth,
        double focusAspectRatio,
        double gutter)
    {
        if (tileCount <= 0 || railWidth <= 0)
        {
            return default;
        }

        var safeMinWidth = minTileWidth > 0 ? minTileWidth : railWidth;
        var safeGutter = Math.Max(0, gutter);
        var safeAspect = focusAspectRatio > 0 ? focusAspectRatio : 1.0;

        var columnsByWidth = Math.Max(1, (int)(railWidth / safeMinWidth));
        var columns = Math.Min(columnsByWidth, tileCount);

        var tileWidth = Math.Max(0, (railWidth - safeGutter * (columns - 1)) / columns);
        var tileHeight = tileWidth / safeAspect;

        var rows = (tileCount + columns - 1) / columns;
        var contentHeight = rows * tileHeight + safeGutter * (rows - 1);

        // A vertical-scrolling ScrollViewer measures its content with PositiveInfinity height; that isn't
        // a real viewport, so it can't tell us anything fits.
        var visibleRows = railHeight <= 0 || tileHeight <= 0 || double.IsPositiveInfinity(railHeight)
            ? 0
            : Math.Min(rows, (int)((railHeight + safeGutter) / (tileHeight + safeGutter)));
        var visibleCount = Math.Min(tileCount, visibleRows * columns);

        return new Geometry(columns, rows, tileWidth, tileHeight, contentHeight, visibleRows, visibleCount, visibleCount < tileCount);
    }

    // The top-left of tile `index` within the rail's content area: row-major fill, left to right before
    // wrapping to the next row.
    public static (double X, double Y) TileOrigin(int index, Geometry geometry, double gutter)
    {
        if (geometry.Columns <= 0)
        {
            return (0, 0);
        }

        var col = index % geometry.Columns;
        var row = index / geometry.Columns;
        var safeGutter = Math.Max(0, gutter);
        return (col * (geometry.TileWidth + safeGutter), row * (geometry.TileHeight + safeGutter));
    }
}
