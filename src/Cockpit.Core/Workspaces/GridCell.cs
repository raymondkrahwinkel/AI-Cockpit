namespace Cockpit.Core.Workspaces;

// A pane's rectangle in a workspace grid: its top-left cell and how many cells it spans. Zero-based.
public readonly record struct GridCell(int Column, int Row, int ColumnSpan = 1, int RowSpan = 1)
{
    // The column just past this cell's right edge.
    public int ColumnEnd => Column + ColumnSpan;

    // The row just past this cell's bottom edge.
    public int RowEnd => Row + RowSpan;

    // Whether this rectangle and `other` share at least one cell.
    public bool Overlaps(GridCell other) =>
        Column < other.ColumnEnd && other.Column < ColumnEnd &&
        Row < other.RowEnd && other.Row < RowEnd;
}
