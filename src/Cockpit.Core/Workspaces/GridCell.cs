namespace Cockpit.Core.Workspaces;

/// <summary>A pane's rectangle in a workspace grid: its top-left cell and how many cells it spans. Zero-based.</summary>
public readonly record struct GridCell(int Column, int Row, int ColumnSpan = 1, int RowSpan = 1)
{
    /// <summary>The column just past this cell's right edge.</summary>
    public int ColumnEnd => Column + ColumnSpan;

    /// <summary>The row just past this cell's bottom edge.</summary>
    public int RowEnd => Row + RowSpan;

    /// <summary>Whether this rectangle and <paramref name="other"/> share at least one cell.</summary>
    public bool Overlaps(GridCell other) =>
        Column < other.ColumnEnd && other.Column < ColumnEnd &&
        Row < other.RowEnd && other.Row < RowEnd;
}
