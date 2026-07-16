using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Cockpit.App.Controls;

/// <summary>
/// Lays the session panels out as an adaptive, draggable grid (Lionear's request, #54 follow-up). One
/// visible pane fills the area; two or more tile in two columns (3–4 → 2×2) unless
/// <see cref="StackVertically"/> transposes it to two rows. The gaps between columns and rows are splitters
/// (drag to re-weight a column's width or a row's height), and each pane's header grip drags it to <b>any</b>
/// cell — including an empty one, which leaves a hole where it came from (dropping onto an occupied cell
/// swaps the two). Single-pane mode (#24 / Zoom) collapses all but the selected pane, which then fills.
///
/// Cells are a sparse list (<see cref="_cells"/>): each entry is a pane's data context or <c>null</c> for a
/// hole, so a pane's position survives a reorder and the free-placement holes are first-class. It is kept
/// separate from the bound collection because moving an item there rebuilds its container (a fresh
/// <c>TtyView</c> with no pty → a black terminal). Column/row weights are <b>positional</b> (a column
/// keeps its width as panes move through it). The geometry (weighting, gutter and cell hit-testing, cell
/// placement) lives in <see cref="StackPaneMath"/> / <see cref="PlaceInCells"/> so it stays unit-testable;
/// this panel owns only the pointer plumbing and the weight/cell stores.
/// </summary>
public sealed class SessionTilePanel : Panel
{
    /// <summary>The draggable gap (px) left between cells; also a splitter's resting thickness.</summary>
    private const double Gutter = 8;

    /// <summary>Extra px on each side of a gutter that still counts as a grab, so the thin gap is easy to catch.</summary>
    private const double GrabTolerance = 4;

    /// <summary>A column/row can't be dragged smaller than this, so a splitter yank never shuts a pane out of view.</summary>
    private const double MinCellExtent = 96;

    /// <summary>Proportional column widths (positional, by column index). Adapts to the current column count.</summary>
    private readonly List<double> _columnWeights = new();

    /// <summary>Proportional row heights (positional, by row index). Adapts to the current row count.</summary>
    private readonly List<double> _rowWeights = new();

    /// <summary>Cell contents by index (row-major, or column-major when stacking vertically): a pane's data context, or null for a hole. Trailing holes are trimmed; interior holes persist.</summary>
    private readonly List<object?> _cells = new();

    /// <summary>Non-null while a splitter drag is in flight. <c>Columns</c> = dragging a vertical gutter (re-weighting columns); otherwise a horizontal gutter (rows).</summary>
    private (bool Columns, int GutterIndex, double[] StartWeights, double StartPos, double ContentExtent)? _resize;

    /// <summary>When true, visible panels stack in a single column (one above the other) instead of the adaptive two-column tiling.</summary>
    public static readonly StyledProperty<bool> StackVerticallyProperty =
        AvaloniaProperty.Register<SessionTilePanel, bool>(nameof(StackVertically));

    static SessionTilePanel()
    {
        AffectsMeasure<SessionTilePanel>(StackVerticallyProperty);
        AffectsArrange<SessionTilePanel>(StackVerticallyProperty);
    }

    public SessionTilePanel()
    {
        // A null background leaves the gutters non-hittable, so the splitter drags would never fire —
        // a transparent fill makes the empty gaps between cells receive pointer input while the panes
        // themselves still render on top.
        Background = Brushes.Transparent;
    }

    public bool StackVertically
    {
        get => GetValue(StackVerticallyProperty);
        set => SetValue(StackVerticallyProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        ReconcileCells();
        var visibleCount = VisibleCount();

        // Single-pane / zoom (#24): the one visible pane fills; the cell layout is bypassed but the cell
        // list is kept so placements return when the grid comes back.
        if (visibleCount <= 1)
        {
            foreach (var child in Children)
            {
                child.Measure(child.IsVisible ? availableSize : default);
            }

            return visibleCount == 0 ? default : availableSize;
        }

        var grid = GridSlots(availableSize.Width, availableSize.Height);
        var byKey = VisibleChildrenByKey();
        foreach (var child in Children)
        {
            if (!child.IsVisible)
            {
                child.Measure(default);
            }
        }

        for (var cell = 0; cell < _cells.Count; cell++)
        {
            if (_cells[cell] is { } key && byKey.TryGetValue(key, out var child))
            {
                var (col, row) = CellOf(cell, grid.Columns, grid.Rows.Count);
                child.Measure(new Size(grid.Cols[col].Height, grid.Rows[row].Height));
            }
        }

        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        ReconcileCells();
        var visibleCount = VisibleCount();

        if (visibleCount <= 1)
        {
            foreach (var child in Children)
            {
                child.Arrange(child.IsVisible ? new Rect(finalSize) : default);
            }

            return finalSize;
        }

        var grid = GridSlots(finalSize.Width, finalSize.Height);
        var byKey = VisibleChildrenByKey();
        foreach (var child in Children)
        {
            if (!child.IsVisible)
            {
                child.Arrange(default);
            }
        }

        for (var cell = 0; cell < _cells.Count; cell++)
        {
            if (_cells[cell] is { } key && byKey.TryGetValue(key, out var child))
            {
                var (col, row) = CellOf(cell, grid.Columns, grid.Rows.Count);
                child.Arrange(new Rect(grid.Cols[col].Top, grid.Rows[row].Top, grid.Cols[col].Height, grid.Rows[row].Height));
            }
        }

        return finalSize;
    }

    /// <summary>The grid cell index (in fill order) under <paramref name="point"/>, clamped to the grid's capacity — including an empty trailing cell, so a pane can be dropped into a hole.</summary>
    public int CellIndexAt(Point point)
    {
        var grid = GridSlots(Bounds.Width, Bounds.Height);
        if (grid.Columns == 0)
        {
            return 0;
        }

        var col = StackPaneMath.SlotAt(grid.Cols, point.X);
        var row = StackPaneMath.SlotAt(grid.Rows, point.Y);
        var index = LinearOf(col, row, grid.Columns, grid.Rows.Count);
        var capacity = grid.Columns * grid.Rows.Count;
        return index < 0 ? 0 : index > capacity - 1 ? capacity - 1 : index;
    }

    /// <summary>The rectangle of grid cell <paramref name="cell"/> (in fill order), for drawing the drop indicator.</summary>
    public Rect CellRect(int cell)
    {
        var grid = GridSlots(Bounds.Width, Bounds.Height);
        if (grid.Columns == 0)
        {
            return default;
        }

        var (col, row) = CellOf(cell, grid.Columns, grid.Rows.Count);
        col = col < 0 ? 0 : col >= grid.Cols.Count ? grid.Cols.Count - 1 : col;
        row = row < 0 ? 0 : row >= grid.Rows.Count ? grid.Rows.Count - 1 : row;
        return new Rect(grid.Cols[col].Top, grid.Rows[row].Top, grid.Cols[col].Height, grid.Rows[row].Height);
    }

    /// <summary>
    /// Places the pane <paramref name="draggedKey"/> into cell <paramref name="cell"/> and re-arranges:
    /// onto a hole it just moves (leaving a hole behind), onto another pane it swaps. Reorders the internal
    /// cell list only — the bound collection and the ItemsControl containers are untouched, so no pane is
    /// rebuilt.
    /// </summary>
    public void PlacePane(object draggedKey, int cell)
    {
        if (PlaceInCells(_cells, draggedKey, cell))
        {
            InvalidateArrange();
            InvalidateMeasure();
        }
    }

    /// <summary>
    /// Pure cell placement: moves <paramref name="dragged"/> to <paramref name="cell"/> within
    /// <paramref name="cells"/>, swapping with whatever is there (a hole leaves a hole behind), padding with
    /// holes to reach the cell, then trimming trailing holes. Returns whether anything changed.
    /// </summary>
    internal static bool PlaceInCells(List<object?> cells, object dragged, int cell)
    {
        while (cells.Count <= cell)
        {
            cells.Add(null);
        }

        var from = cells.IndexOf(dragged);
        if (from < 0 || from == cell)
        {
            TrimTrailingHoles(cells);
            return false;
        }

        cells[from] = cells[cell];
        cells[cell] = dragged;
        TrimTrailingHoles(cells);
        return true;
    }

    private static void TrimTrailingHoles(List<object?> cells)
    {
        while (cells.Count > 0 && cells[^1] is null)
        {
            cells.RemoveAt(cells.Count - 1);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_resize is { } drag)
        {
            var pos = drag.Columns ? p.X : p.Y;
            var updated = StackPaneMath.Resize(drag.StartWeights, drag.GutterIndex, pos - drag.StartPos, drag.ContentExtent, MinCellExtent);
            var target = drag.Columns ? _columnWeights : _rowWeights;
            target.Clear();
            target.AddRange(updated);
            InvalidateArrange();
            e.Handled = true;
            return;
        }

        // Idle hover: show the matching resize cursor only while over a gutter so the affordance is discoverable.
        Cursor = ColumnGutterAt(p.X) >= 0
            ? new Cursor(StandardCursorType.SizeWestEast)
            : RowGutterAt(p.Y) >= 0
                ? new Cursor(StandardCursorType.SizeNorthSouth)
                : Cursor.Default;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        // A splitter drag starts only on a press that lands on the panel itself — the empty gutter between
        // cells. A press on a child (the reorder grip, the terminal, a header button) has that child as the
        // source and is left alone, so grabbing the grip reorders instead of fighting it for the pointer.
        if (e.Handled
            || !ReferenceEquals(e.Source, this)
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var p = e.GetPosition(this);
        var grid = GridSlots(Bounds.Width, Bounds.Height);

        var columnGutter = ColumnGutterAt(p.X);
        if (columnGutter >= 0)
        {
            _resize = (true, columnGutter, ToArray(_columnWeights), p.X, Bounds.Width - Gutter * (grid.Columns - 1));
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        var rowGutter = RowGutterAt(p.Y);
        if (rowGutter >= 0)
        {
            _resize = (false, rowGutter, ToArray(_rowWeights), p.Y, Bounds.Height - Gutter * (grid.Rows.Count - 1));
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_resize is not null)
        {
            _resize = null;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    /// <summary>The vertical-gutter index (between two columns) under <paramref name="x"/>, or -1. Only present with 2+ columns.</summary>
    private int ColumnGutterAt(double x)
    {
        var grid = GridSlots(Bounds.Width, Bounds.Height);
        return grid.Columns < 2 ? -1 : StackPaneMath.GutterAt(grid.Cols, x, Gutter, GrabTolerance);
    }

    /// <summary>The horizontal-gutter index (between two rows) under <paramref name="y"/>, or -1. Only present with 2+ rows.</summary>
    private int RowGutterAt(double y)
    {
        var grid = GridSlots(Bounds.Width, Bounds.Height);
        return grid.Rows.Count < 2 ? -1 : StackPaneMath.GutterAt(grid.Rows, y, Gutter, GrabTolerance);
    }

    /// <summary>Column/row slot geometry for the current cell count, reconciling the positional weight arrays to the current dimensions.</summary>
    private (IReadOnlyList<StackPaneMath.Slot> Cols, IReadOnlyList<StackPaneMath.Slot> Rows, int Columns) GridSlots(double width, double height)
    {
        var (columns, rows) = Dimensions(_cells.Count, StackVertically);
        if (columns == 0)
        {
            return (System.Array.Empty<StackPaneMath.Slot>(), System.Array.Empty<StackPaneMath.Slot>(), 0);
        }

        EnsureAxis(_columnWeights, columns);
        EnsureAxis(_rowWeights, rows);
        return (StackPaneMath.Layout(_columnWeights, width, Gutter), StackPaneMath.Layout(_rowWeights, height, Gutter), columns);
    }

    /// <summary>
    /// Maps a cell index to its (column, row) for the current fill order: column-major when stacking
    /// vertically (a column fills top-to-bottom before the next starts), row-major otherwise.
    /// </summary>
    private (int Col, int Row) CellOf(int index, int columns, int rows)
    {
        var span = rows < 1 ? 1 : rows;
        var cols = columns < 1 ? 1 : columns;
        return StackVertically ? (index / span, index % span) : (index % cols, index / cols);
    }

    /// <summary>Inverse of <see cref="CellOf"/>: the cell index at a given column/row.</summary>
    private int LinearOf(int col, int row, int columns, int rows) =>
        StackVertically ? col * rows + row : row * columns + col;

    /// <summary>
    /// Reconciles the cell list with the live panes: drops closed sessions (leaving holes), and gives each
    /// new pane the first hole or a new trailing cell. Runs off <b>all</b> children (visible or collapsed by
    /// zoom) so a placement isn't lost when the grid is temporarily single-pane.
    /// </summary>
    private void ReconcileCells()
    {
        var live = new HashSet<object>();
        foreach (var child in Children)
        {
            if (child.DataContext is { } key)
            {
                live.Add(key);
            }
        }

        for (var i = 0; i < _cells.Count; i++)
        {
            if (_cells[i] is { } key && !live.Contains(key))
            {
                _cells[i] = null;
            }
        }

        var present = new HashSet<object>();
        foreach (var cell in _cells)
        {
            if (cell is { } key)
            {
                present.Add(key);
            }
        }

        foreach (var child in Children)
        {
            if (child.DataContext is { } key && present.Add(key))
            {
                PlaceInFirstHole(key);
            }
        }

        TrimTrailingHoles(_cells);
    }

    private void PlaceInFirstHole(object key)
    {
        for (var i = 0; i < _cells.Count; i++)
        {
            if (_cells[i] is null)
            {
                _cells[i] = key;
                return;
            }
        }

        _cells.Add(key);
    }

    private Dictionary<object, Control> VisibleChildrenByKey()
    {
        var map = new Dictionary<object, Control>();
        foreach (var child in Children)
        {
            if (child.IsVisible && child.DataContext is { } key)
            {
                map[key] = child;
            }
        }

        return map;
    }

    /// <summary>Pads with equal 1.0 weights or trims extras so the positional weight list matches the axis's current length.</summary>
    private static void EnsureAxis(List<double> weights, int count)
    {
        while (weights.Count < count)
        {
            weights.Add(1.0);
        }

        while (weights.Count > count)
        {
            weights.RemoveAt(weights.Count - 1);
        }
    }

    private static double[] ToArray(List<double> weights) => weights.ToArray();

    private int VisibleCount()
    {
        var count = 0;
        foreach (var child in Children)
        {
            if (child.IsVisible)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Columns/rows for a cell count. Adaptive default: one fills, two+ cap at two <b>columns</b> and grow
    /// downwards, filling row by row (3–4 → 2×2). <paramref name="stackVertically"/> is the exact transpose —
    /// it caps at two <b>rows</b> and grows sideways, filling column by column. The fill order that pairs with
    /// this lives in <see cref="CellOf"/>.
    /// </summary>
    public static (int Columns, int Rows) Dimensions(int cellCount, bool stackVertically = false)
    {
        if (cellCount <= 0)
        {
            return (0, 0);
        }

        if (stackVertically)
        {
            var rows = cellCount <= 1 ? 1 : 2;
            return ((cellCount + rows - 1) / rows, rows);
        }

        var columns = cellCount <= 1 ? 1 : 2;
        return (columns, (cellCount + columns - 1) / columns);
    }
}
