namespace Cockpit.Plugin.FanOut;

/// <summary>
/// Places a fan-out's sessions on a grid that stays readable at two to five of them side by side. Three tiles
/// abreast is the widest a session pane stays legible at, so rows are filled to at most that and the remainder
/// spread evenly over the rows rather than dumped in a short last one — five reads as 3 + 2, not 3 + 1 + 1.
/// </summary>
/// <remarks>
/// The grid's column count is the least common multiple of the row sizes, so a row of two and a row of three
/// both divide it exactly: every tile in a row is the same width and no row ends in a hole. Five tiles give a
/// six-column grid — spans of two on the top row, three on the bottom.
/// </remarks>
public static class FanOutTileLayout
{
    /// <summary>The widest a row gets before the layout adds another row: past three, a session pane is too narrow to read.</summary>
    private const int MaxTilesPerRow = 3;

    public static FanOutLayout For(int count)
    {
        if (count <= 0)
        {
            return new FanOutLayout(1, 1, []);
        }

        var rowSizes = _DistributeOverRows(count);
        var columns = rowSizes.Aggregate(1, _LeastCommonMultiple);

        var tiles = new List<FanOutTile>(count);
        for (var row = 0; row < rowSizes.Count; row++)
        {
            var span = columns / rowSizes[row];
            for (var position = 0; position < rowSizes[row]; position++)
            {
                tiles.Add(new FanOutTile(position * span, row, span));
            }
        }

        return new FanOutLayout(columns, rowSizes.Count, tiles);
    }

    /// <summary>
    /// How many tiles each row holds: as few rows as <see cref="MaxTilesPerRow"/> allows, then the tiles spread
    /// as evenly as possible over them, the earlier rows taking the remainder so the fuller row is on top.
    /// </summary>
    private static IReadOnlyList<int> _DistributeOverRows(int count)
    {
        var rows = (count + MaxTilesPerRow - 1) / MaxTilesPerRow;
        var even = count / rows;
        var remainder = count % rows;

        return Enumerable.Range(0, rows)
            .Select(row => even + (row < remainder ? 1 : 0))
            .ToList();
    }

    private static int _LeastCommonMultiple(int left, int right) => left / _GreatestCommonDivisor(left, right) * right;

    private static int _GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return left;
    }
}
