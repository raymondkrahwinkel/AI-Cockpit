namespace Cockpit.Core.Workspaces;

/// <summary>
/// Where a newly added widget lands on a dashboard, and how tall the dashboard has to be to show what it
/// holds. Pure math over rectangles — no Avalonia, no view models — so the placement rules are testable on
/// their own, the same way <c>StackPaneMath</c> keeps the session grid's arithmetic out of the panel.
/// </summary>
/// <remarks>
/// The rule is row-major first-fit: scan left-to-right, top-to-bottom, take the first free rectangle that
/// fits within the column count. When nothing fits, the grid gains a row (Raymond's "2x2" is a starting
/// shape, not a cap — see <see cref="DashboardLayout.Rows"/>). Free placement with holes is preserved:
/// dragging a widget elsewhere leaves a gap, and first-fit will reuse that gap for the next widget rather
/// than always appending at the bottom.
/// </remarks>
public static class DashboardGridMath
{
    /// <summary>
    /// The cell a widget spanning <paramref name="columnSpan"/>×<paramref name="rowSpan"/> should occupy,
    /// given what is already placed. Never returns an overlapping cell; grows past
    /// <paramref name="layout"/>'s row count when the existing rows are full.
    /// </summary>
    /// <param name="occupied">The rectangles already on the dashboard.</param>
    /// <param name="layout">The dashboard's grid settings — only <see cref="DashboardLayout.Columns"/> constrains placement.</param>
    /// <param name="columnSpan">Requested width in cells; clamped to at least 1 and at most the column count.</param>
    /// <param name="rowSpan">Requested height in cells; clamped to at least 1.</param>
    public static GridCell PlaceNext(IReadOnlyCollection<GridCell> occupied, DashboardLayout layout, int columnSpan = 1, int rowSpan = 1)
    {
        var columns = layout.Clamped().Columns;
        var width = Math.Clamp(columnSpan, 1, columns);
        var height = Math.Max(1, rowSpan);

        // Scanning one row past the current content guarantees a hit: that row is empty by construction, so
        // the loop always terminates with a free cell rather than needing an "and if not, then…" fallback.
        var lastRow = occupied.Count == 0 ? 0 : occupied.Max(cell => cell.RowEnd);
        for (var row = 0; row <= lastRow; row++)
        {
            for (var column = 0; column + width <= columns; column++)
            {
                var candidate = new GridCell(column, row, width, height);
                if (!occupied.Any(candidate.Overlaps))
                {
                    return candidate;
                }
            }
        }

        return new GridCell(0, lastRow, width, height);
    }

    /// <summary>
    /// How many rows the dashboard must render to show every placed widget — at least
    /// <see cref="DashboardLayout.Rows"/>, more once the content has grown past it. The view binds its row
    /// count to this rather than to the setting, which is what makes "Rows" a starting height instead of a cap.
    /// </summary>
    public static int RequiredRows(IReadOnlyCollection<GridCell> occupied, DashboardLayout layout)
    {
        var configured = layout.Clamped().Rows;
        return occupied.Count == 0 ? configured : Math.Max(configured, occupied.Max(cell => cell.RowEnd));
    }

    /// <summary>
    /// The cell the pointer is over, from a position inside the grid. The inverse of the view's layout: the
    /// grid draws equal columns and rows, so which cell a drop lands in is arithmetic rather than hit-testing —
    /// and doing it here keeps the drag's rules testable instead of buried in a pointer handler.
    /// </summary>
    /// <param name="x">Pointer X within the grid.</param>
    /// <param name="y">Pointer Y within the grid.</param>
    /// <param name="width">The grid's width.</param>
    /// <param name="height">The grid's height.</param>
    /// <param name="columns">How many columns it draws.</param>
    /// <param name="rows">How many rows it draws.</param>
    public static (int Column, int Row)? CellAt(double x, double y, double width, double height, int columns, int rows)
    {
        if (columns <= 0 || rows <= 0 || width <= 0 || height <= 0 || x < 0 || y < 0 || x >= width || y >= height)
        {
            return null;
        }

        return (Math.Clamp((int)(x / (width / columns)), 0, columns - 1),
                Math.Clamp((int)(y / (height / rows)), 0, rows - 1));
    }

    /// <summary>
    /// Where every pane ends up when <paramref name="paneId"/> is dropped on <paramref name="target"/>. Free
    /// placement with holes, the same as the session grid: an empty cell simply takes the pane, and an occupied
    /// one swaps the two. Dropping a pane on itself changes nothing.
    /// </summary>
    /// <remarks>
    /// Returns the whole new arrangement rather than mutating, so the caller persists one settled state — and a
    /// swap can never half-apply, leaving two panes stacked on one cell.
    /// </remarks>
    public static IReadOnlyList<(string Id, GridCell Cell)> Drop(
        IReadOnlyList<(string Id, GridCell Cell)> panes, string paneId, (int Column, int Row) target)
    {
        var dragged = panes.FirstOrDefault(pane => pane.Id == paneId);
        if (dragged.Id is null || (dragged.Cell.Column == target.Column && dragged.Cell.Row == target.Row))
        {
            return panes;
        }

        var landing = dragged.Cell with { Column = target.Column, Row = target.Row };
        var occupant = panes.FirstOrDefault(pane => pane.Id != paneId && pane.Cell.Overlaps(landing));

        return [.. panes.Select(pane =>
            pane.Id == paneId ? (pane.Id, landing)
            : occupant.Id is not null && pane.Id == occupant.Id ? (pane.Id, occupant.Cell with { Column = dragged.Cell.Column, Row = dragged.Cell.Row })
            : pane)];
    }

    /// <summary>
    /// The size a pane takes when its corner is dragged to <paramref name="corner"/> — the cell the pointer is
    /// over becomes the pane's new bottom-right. Null when the result would not be a legal size: off the grid,
    /// inverted (dragged above or left of the pane's own origin), or overlapping a neighbour.
    /// </summary>
    /// <remarks>
    /// Refusing rather than clamping is what makes the drag feel solid: the pane simply stops growing at the
    /// obstacle and keeps its last good size, instead of jumping over a neighbour or snapping to a size the
    /// pointer is nowhere near.
    /// </remarks>
    public static GridCell? Resize(
        IReadOnlyList<(string Id, GridCell Cell)> panes, string paneId, (int Column, int Row) corner, DashboardLayout layout)
    {
        var pane = panes.FirstOrDefault(entry => entry.Id == paneId);
        if (pane.Id is null)
        {
            return null;
        }

        var columns = layout.Clamped().Columns;
        var resized = pane.Cell with
        {
            ColumnSpan = corner.Column - pane.Cell.Column + 1,
            RowSpan = corner.Row - pane.Cell.Row + 1,
        };

        if (resized.ColumnSpan < 1 || resized.RowSpan < 1 || resized.ColumnEnd > columns)
        {
            return null;
        }

        return panes.Any(other => other.Id != paneId && other.Cell.Overlaps(resized)) ? null : resized;
    }
}
