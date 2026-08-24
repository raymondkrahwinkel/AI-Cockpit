namespace Cockpit.Core.Workspaces;

// AC-1013: where a new widget lands, and how tall the dashboard must be. Pure rectangle math (no Avalonia/view
// models), same split as `StackPaneMath`. Rule: row-major first-fit, growing rows when full (Rows is a starting
// shape not a cap); holes from moved widgets are reused rather than always appending at the bottom. (Full text on ticket.)
public static class DashboardGridMath
{
    // AC-1013: the cell a `columnSpan`×`rowSpan` widget should occupy given `occupied`; never overlaps, grows
    // past `layout`'s row count when full. Only `DashboardLayout.Columns` constrains placement; spans are
    // clamped to at least 1 (columnSpan also to at most the column count).
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

    // How many rows the dashboard must render to show every placed widget — at least
    // `DashboardLayout.Rows`, more once the content has grown past it. The view binds its row
    // count to this rather than to the setting, which is what makes "Rows" a starting height instead of a cap.
    public static int RequiredRows(IReadOnlyCollection<GridCell> occupied, DashboardLayout layout)
    {
        var configured = layout.Clamped().Rows;
        return occupied.Count == 0 ? configured : Math.Max(configured, occupied.Max(cell => cell.RowEnd));
    }

    // AC-1013: the cell the pointer is over, given position/size/columns/rows. Inverse of the view's equal-cell
    // layout, so a drop's target cell is arithmetic rather than hit-testing — keeps the drag rules testable
    // instead of buried in a pointer handler.
    public static (int Column, int Row)? CellAt(double x, double y, double width, double height, int columns, int rows)
    {
        if (columns <= 0 || rows <= 0 || width <= 0 || height <= 0 || x < 0 || y < 0 || x >= width || y >= height)
        {
            return null;
        }

        return (Math.Clamp((int)(x / (width / columns)), 0, columns - 1),
                Math.Clamp((int)(y / (height / rows)), 0, rows - 1));
    }

    // AC-1013: where every pane ends up when `paneId` is dropped on `target` — empty cell takes it, occupied
    // cell swaps. Null (like `Resize`) when refused: off-grid, covers more than one pane, or the swap partner
    // can't fit the vacated spot. Returns the whole new arrangement (never mutates) so a swap can't half-apply.
    public static IReadOnlyList<(string Id, GridCell Cell)>? Drop(
        IReadOnlyList<(string Id, GridCell Cell)> panes,
        string paneId,
        (int Column, int Row) target,
        DashboardLayout layout)
    {
        var dragged = panes.FirstOrDefault(pane => pane.Id == paneId);
        if (dragged.Id is null)
        {
            return null;
        }

        if (dragged.Cell.Column == target.Column && dragged.Cell.Row == target.Row)
        {
            return panes;
        }

        var columns = layout.Clamped().Columns;
        var landing = dragged.Cell with { Column = target.Column, Row = target.Row };

        // AC-1013: only columns are a wall — rows grow via RequiredRows (same asymmetry as Resize), so dragging
        // past the last row is how the operator grows the dashboard.
        if (landing.ColumnEnd > columns)
        {
            return null;
        }

        var covered = panes.Where(pane => pane.Id != paneId && pane.Cell.Overlaps(landing)).ToList();
        if (covered.Count > 1)
        {
            // A swap is an answer to one occupant. Over two there is no single pane to trade places with, and
            // moving whichever came first out of the way leaves the other one underneath the dragged pane —
            // stacked, and persisted that way.
            return null;
        }

        if (covered.Count == 0)
        {
            return [.. panes.Select(pane => pane.Id == paneId ? (pane.Id, landing) : pane)];
        }

        // The occupant takes the dragged pane's origin at its own size, and that is not always somewhere it fits:
        // a wide pane trading with a narrow one needs room the narrow one never took up.
        var occupant = covered[0];
        var vacated = occupant.Cell with { Column = dragged.Cell.Column, Row = dragged.Cell.Row };
        if (vacated.ColumnEnd > columns
            || vacated.Overlaps(landing)
            || panes.Any(pane => pane.Id != paneId && pane.Id != occupant.Id && pane.Cell.Overlaps(vacated)))
        {
            return null;
        }

        return [.. panes.Select(pane =>
            pane.Id == paneId ? (pane.Id, landing)
            : pane.Id == occupant.Id ? (pane.Id, vacated)
            : pane)];
    }

    // AC-1013: pane size when its corner is dragged to `corner` (pointer cell becomes new bottom-right). Null
    // when illegal: off-grid, inverted, or overlapping a neighbour. Refusing (not clamping) makes the drag feel
    // solid — it stops at the obstacle instead of jumping over a neighbour or snapping to a distant size.
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
