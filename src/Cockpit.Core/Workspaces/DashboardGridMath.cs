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
}
