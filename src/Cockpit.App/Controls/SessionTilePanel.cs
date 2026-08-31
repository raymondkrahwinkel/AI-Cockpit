using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.Core.Layout;
using Cockpit.Core.Shortcuts;

namespace Cockpit.App.Controls;

// AC-1013 (#54 follow-up, Lionear's request): adaptive draggable session grid — splitters re-weight
// columns/rows, header grips drag panes between cells (including empty ones, leaving/filling holes),
// single-pane Zoom (#24) collapses to the selected pane. Rationale (sparse `_cells`, off-collection moves to avoid pty rebuilds, positional weights) belongs on the ticket.
public sealed class SessionTilePanel : Panel
{
    // The draggable gap (px) left between cells; also a splitter's resting thickness.
    private const double Gutter = 8;

    // Extra px on each side of a gutter that still counts as a grab, so the thin gap is easy to catch.
    private const double GrabTolerance = 4;

    // A column/row can't be dragged smaller than this, so a splitter yank never shuts a pane out of view.
    private const double MinCellExtent = 96;

    // Proportional column widths (positional, by column index). Adapts to the current column count.
    private readonly List<double> _columnWeights = new();

    // Proportional row heights (positional, by row index). Adapts to the current row count.
    private readonly List<double> _rowWeights = new();

    // Cell contents by index (row-major, or column-major when stacking vertically): a pane's data context, or null for a hole. Trailing holes are trimmed; interior holes persist.
    private readonly List<object?> _cells = new();

    // Non-null while a splitter drag is in flight. `Columns` = dragging a vertical gutter (re-weighting columns); otherwise a horizontal gutter (rows).
    private (bool Columns, int GutterIndex, double[] StartWeights, double StartPos, double ContentExtent)? _resize;

    // When true, visible panels stack in a single column (one above the other) instead of the adaptive two-column tiling.
    public static readonly StyledProperty<bool> StackVerticallyProperty =
        AvaloniaProperty.Register<SessionTilePanel, bool>(nameof(StackVertically));

    // AC-1013: when true, the focus candidate fills most of the panel and the rest auto-fit into a
    // scrolling rail (AC-441/444), using `RailLayoutMath`/`StackPaneMath` directly over this panel's own
    // children (not a nested `ItemsControl`) because promoting a pane to focus must not rebuild it (AC-442).
    public static readonly StyledProperty<bool> FocusRailLayoutProperty =
        AvaloniaProperty.Register<SessionTilePanel, bool>(nameof(FocusRailLayout));

    // Weight of the rail against the focus pane's fixed 1.0, mirroring `FocusRailPanel.RailWeight`.
    public static readonly StyledProperty<double> RailWeightProperty =
        AvaloniaProperty.Register<SessionTilePanel, double>(nameof(RailWeight), LayoutSettings.DefaultFocusRailWeight);

    // AC-1013: set via a Style Setter (`CockpitView.axaml`, like `IsPaneVisible`) from
    // `SessionPanelViewModel.IsSelected`, so the panel picks its focus child without knowing the view model
    // type. `AffectsParentArrange`/`AffectsParentMeasure` invalidate the owner on an attached-value change.
    public static readonly AttachedProperty<bool> IsFocusCandidateProperty =
        AvaloniaProperty.RegisterAttached<SessionTilePanel, Control, bool>("IsFocusCandidate");

    // Set the same way, from `SessionPanelViewModel.RailSortKey` — attention-needing sessions first, then
    // the sidebar's own order (AC-444 #2). The rail sorts its non-focus children by this key.
    public static readonly AttachedProperty<int> RailSortKeyProperty =
        AvaloniaProperty.RegisterAttached<SessionTilePanel, Control, int>("RailSortKey");

    // Set the same way, from `SessionPanelViewModel.IsOnActiveDesk` — the tab now showing owns this pane, zoom
    // or no zoom. The grid sizes itself from these alone: it holds a container for every session alive, so
    // counting all of them laid a two-pane tab out as a 2×2 (AC-696). True by default: no property, no filter.
    public static readonly AttachedProperty<bool> IsOnActiveDeskProperty =
        AvaloniaProperty.RegisterAttached<SessionTilePanel, Control, bool>("IsOnActiveDesk", defaultValue: true);

    // AC-670: the two boxes a rail tile is drawn from, written onto the container and read by `MiniatureHost`
    // inside its template — `inherits: true` is what carries them across that boundary, and two boxes rather
    // than one scale because only the host can measure the pane chrome between them (see `MiniatureHost.Fit`).
    public static readonly AttachedProperty<Size> MiniatureTileSizeProperty =
        AvaloniaProperty.RegisterAttached<SessionTilePanel, Control, Size>("MiniatureTileSize", inherits: true);

    public static readonly AttachedProperty<Size> MiniatureFocusSizeProperty =
        AvaloniaProperty.RegisterAttached<SessionTilePanel, Control, Size>("MiniatureFocusSize", inherits: true);

    // AC-923: the box the focus pane's own host actually got arranged into this pass, read back straight off
    // its `Bounds` rather than reconstructed — see `MiniatureHost.Fit`'s fallback and the PR description.
    public static readonly AttachedProperty<Size> MiniatureFocusChildBoxProperty =
        AvaloniaProperty.RegisterAttached<SessionTilePanel, Control, Size>("MiniatureFocusChildBox", inherits: true);

    // AC-670: true for every control inside a rail tile, inherited for the same reason as the boxes above, and
    // read by `CockpitView.axaml` to strip a miniature down to the terminal.
    public static readonly AttachedProperty<bool> IsMiniatureProperty =
        AvaloniaProperty.RegisterAttached<SessionTilePanel, Control, bool>("IsMiniature", inherits: true);

    public static bool GetIsFocusCandidate(Control element) => element.GetValue(IsFocusCandidateProperty);

    public static void SetIsFocusCandidate(Control element, bool value) => element.SetValue(IsFocusCandidateProperty, value);

    public static bool GetIsOnActiveDesk(Control element) => element.GetValue(IsOnActiveDeskProperty);

    public static void SetIsOnActiveDesk(Control element, bool value) => element.SetValue(IsOnActiveDeskProperty, value);

    public static int GetRailSortKey(Control element) => element.GetValue(RailSortKeyProperty);

    public static void SetRailSortKey(Control element, int value) => element.SetValue(RailSortKeyProperty, value);

    public static Size GetMiniatureTileSize(Control element) => element.GetValue(MiniatureTileSizeProperty);

    public static Size GetMiniatureFocusSize(Control element) => element.GetValue(MiniatureFocusSizeProperty);

    public static Size GetMiniatureFocusChildBox(Control element) => element.GetValue(MiniatureFocusChildBoxProperty);

    public static void SetMiniatureFocusChildBox(Control element, Size value) => element.SetValue(MiniatureFocusChildBoxProperty, value);

    public static bool GetIsMiniature(Control element) => element.GetValue(IsMiniatureProperty);

    // Writes all three halves of "this pane is a rail tile" at once — the two boxes it is drawn from and the flag
    // the markup strips its chrome by — so they can never disagree. An empty focus box means "not in a rail".
    public static void SetMiniatureBox(Control element, Size tile, Size focus)
    {
        var miniature = focus is { Width: > 0, Height: > 0 };
        element.SetValue(MiniatureTileSizeProperty, tile);
        element.SetValue(MiniatureFocusSizeProperty, focus);
        element.SetValue(IsMiniatureProperty, miniature);
    }

    // How far the rail has been scrolled (px), when more tiles exist than fit — the rail's one scroll axis
    // (AC-441: "hoogte de rijen, de rest scrollt verticaal"). Clamped to the content extent on every arrange,
    // so a window resize or a session closing can never leave it stranded past the new bottom.
    private double _railScrollOffset;

    private const double RailScrollStep = 40;

    static SessionTilePanel()
    {
        AffectsMeasure<SessionTilePanel>(StackVerticallyProperty, FocusRailLayoutProperty, RailWeightProperty);
        AffectsArrange<SessionTilePanel>(StackVerticallyProperty, FocusRailLayoutProperty, RailWeightProperty);
        AffectsParentMeasure<SessionTilePanel>(IsFocusCandidateProperty, RailSortKeyProperty, IsOnActiveDeskProperty);
        AffectsParentArrange<SessionTilePanel>(IsFocusCandidateProperty, RailSortKeyProperty, IsOnActiveDeskProperty);
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

    public bool FocusRailLayout
    {
        get => GetValue(FocusRailLayoutProperty);
        set => SetValue(FocusRailLayoutProperty, value);
    }

    public double RailWeight
    {
        get => GetValue(RailWeightProperty);
        set => SetValue(RailWeightProperty, value);
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
                _LeaveTheRail(child);
                child.Measure(child.IsVisible ? availableSize : default);
            }

            return visibleCount == 0 ? default : availableSize;
        }

        if (FocusRailLayout)
        {
            return _MeasureFocusRail(availableSize);
        }

        foreach (var child in Children)
        {
            _LeaveTheRail(child);
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
            ClipToBounds = false;
            foreach (var child in Children)
            {
                child.Arrange(child.IsVisible ? new Rect(finalSize) : default);
            }

            return finalSize;
        }

        if (FocusRailLayout)
        {
            return _ArrangeFocusRail(finalSize);
        }

        ClipToBounds = false;
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

    // The grid cell index (in fill order) under `point`, clamped to the grid's capacity — including an empty trailing cell, so a pane can be dropped into a hole.
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

    // The rectangle of grid cell `cell` (in fill order), for drawing the drop indicator.
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

    // AC-1013: places `draggedKey` into `cell` — onto a hole it moves (leaving a hole behind), onto a
    // pane it swaps. Reorders the internal cell list only; bound collection/containers stay untouched.
    public void PlacePane(object draggedKey, int cell)
    {
        if (PlaceInCells(_cells, draggedKey, cell))
        {
            InvalidateArrange();
            InvalidateMeasure();
        }
    }

    // AC-1013: the pane spatially adjacent to `active` in `direction`, skipping holes, or null at a grid
    // edge / if `active` isn't placed. A geometry read only — the caller moves selection, nothing reparents.
    public object? NeighbourInDirection(object active, PaneDirection direction)
    {
        if (FocusRailLayout)
        {
            return _NeighbourInFocusRail(active, direction);
        }

        ReconcileCells();
        var fromCell = _cells.IndexOf(active);
        if (fromCell < 0)
        {
            return null;
        }

        var occupied = new bool[_cells.Count];
        for (var i = 0; i < _cells.Count; i++)
        {
            occupied[i] = _cells[i] is not null;
        }

        return NeighbourCell(occupied, fromCell, direction, StackVertically) is { } cell ? _cells[cell] : null;
    }

    // --- Focus + rail (AC-441/444) -----------------------------------------------------------------------
    // AC-1013: cached from the last measure pass (like `RailTilePanel.Geometry`) so arrange, wheel scroll
    // and keyboard nav agree with what was measured — a mismatch would resize a pty twice (AC-442 forbids).
    private readonly record struct FocusRailLayoutResult(
        Control Focus,
        IReadOnlyList<Control> Rail,
        StackPaneMath.Slot FocusSlot,
        StackPaneMath.Slot RailSlot,
        RailLayoutMath.Geometry Geometry);

    private FocusRailLayoutResult? _focusRailLayout;

    // AC-1264: the focus pane's own box, as the last arrange read it back. Handed to the tiles from measure,
    // never from inside an arrange: `MiniatureHost.FocusChildBox` carries `AffectsMeasure`, so writing it there
    // asks the running pass for a measure off a value that same pass produced — no fixed point once it moves.
    private Size _focusChildBox;

    private Size _MeasureFocusRail(Size availableSize)
    {
        foreach (var child in Children)
        {
            if (!child.IsVisible)
            {
                child.Measure(default);
            }
        }

        var (focus, rail) = _RailChildren();
        if (focus is null)
        {
            _focusRailLayout = null;
            return availableSize;
        }

        var slots = StackPaneMath.Layout([1.0, RailWeight], availableSize.Width, Gutter);
        var focusSlot = slots[0];
        var railSlot = slots[1];

        _LeaveTheRail(focus);
        focus.Measure(new Size(focusSlot.Height, availableSize.Height));

        if (rail.Count == 0)
        {
            _focusRailLayout = new FocusRailLayoutResult(focus, rail, focusSlot, railSlot, default);
            return availableSize;
        }

        // A tile mirrors the focus pane's own shape (RailLayoutMath's contract, AC-442's comment to AC-443:
        // "de invariant houdt alleen als de tegel de focuspane × s is") — derived fresh from the actual slot
        // rather than a settable property, so it can never go stale against a divider drag.
        var aspect = availableSize.Height > 0 ? focusSlot.Height / availableSize.Height : 1.0;

        // AC-670 #2: handing `RailLayoutMath` the rail's own width as the minimum tile width is the whole
        // one-column rule — a tile is never narrower than the rail, so a second column never fits.
        var geometry = RailLayoutMath.Compute(railSlot.Height, availableSize.Height, rail.Count, railSlot.Height, aspect, Gutter);
        var tileSize = new Size(geometry.TileWidth, geometry.TileHeight);

        // Both boxes, not a scale: only the host behind the pane's own chrome can tell what that chrome costs,
        // and the pty depends on getting that exactly right (AC-670, `MiniatureHost.Fit`).
        var focusSize = new Size(focusSlot.Height, availableSize.Height);
        foreach (var tile in rail)
        {
            SetMiniatureBox(tile, tileSize, focusSize);
            SetMiniatureFocusChildBox(tile, _focusChildBox);
            tile.Measure(tileSize);
        }

        _focusRailLayout = new FocusRailLayoutResult(focus, rail, focusSlot, railSlot, geometry);
        return availableSize;
    }

    private Size _ArrangeFocusRail(Size finalSize)
    {
        foreach (var child in Children)
        {
            if (!child.IsVisible)
            {
                child.Arrange(default);
            }
        }

        if (_focusRailLayout is not { } layout)
        {
            ClipToBounds = false;
            return finalSize;
        }

        layout.Focus.Arrange(new Rect(layout.FocusSlot.Top, 0, layout.FocusSlot.Height, finalSize.Height));

        if (layout.Rail.Count == 0)
        {
            ClipToBounds = false;
            return finalSize;
        }

        // AC-923: the focus pane's own host just arranged for real, one line up — every rail tile gets its
        // exact box instead of reconstructing an approximation of it (see PR description). AC-1264: kept for the
        // next measure rather than written here, and a box that moved asks for that pass from outside this one.

        // AC-1266: tiles stay one measure behind a moving focus subtree. A 20 px probe changed a real terminal
        // from 80x34 to 80x35 but rendered 0 of 540,000 pixels differently, so another layout round is not worth it.
        var focusChildBox = layout.Focus.GetVisualDescendants().OfType<MiniatureHost>().FirstOrDefault()?.Bounds.Size
            ?? default;
        if (focusChildBox != _focusChildBox)
        {
            _focusChildBox = focusChildBox;
            Dispatcher.UIThread.Post(InvalidateMeasure);
        }

        // Width decides the columns (measured above), height decides how many rows show before the rest
        // scrolls (AC-441) — clamped here too, not just on the wheel, so a window resize or a session
        // closing can never leave the scroll stranded past the new bottom.
        var maxScroll = Math.Max(0, layout.Geometry.ContentHeight - finalSize.Height);
        _railScrollOffset = Math.Clamp(_railScrollOffset, 0, maxScroll);

        var tileSize = new Size(layout.Geometry.TileWidth, layout.Geometry.TileHeight);
        for (var i = 0; i < layout.Rail.Count; i++)
        {
            var (x, y) = RailLayoutMath.TileOrigin(i, layout.Geometry, Gutter);
            layout.Rail[i].Arrange(new Rect(layout.RailSlot.Top + x, y - _railScrollOffset, tileSize.Width, tileSize.Height));
        }

        // The panel's own bounds already match the focus pane's (arranged within `[0, finalSize.Height]`
        // too), so clipping the whole panel to itself clips only what a scrolled-off rail tile would
        // otherwise overdraw — no separate clip region for just the rail sub-area is needed.
        ClipToBounds = true;
        return finalSize;
    }

    // Every visible child, focus first: the one carrying `IsFocusCandidate` (the operator's current
    // selection), or the first child when none does (no selection yet) — the panel never renders nothing.
    // The rest sort by `RailSortKey` (attention-needing first, then the sidebar's own order, AC-444 #2).
    private (Control? Focus, IReadOnlyList<Control> Rail) _RailChildren()
    {
        Control? focus = null;
        var visible = new List<Control>();
        foreach (var child in Children)
        {
            if (!child.IsVisible)
            {
                continue;
            }

            visible.Add(child);
            if (focus is null && GetIsFocusCandidate(child))
            {
                focus = child;
            }
        }

        if (visible.Count == 0)
        {
            return (null, Array.Empty<Control>());
        }

        focus ??= visible[0];
        var rail = visible.Where(child => !ReferenceEquals(child, focus)).OrderBy(GetRailSortKey).ToList();
        return (focus, rail);
    }

    // Undoes a previous rail-mode box when a pane is no longer a tile (the grid, single-pane, the focus slot,
    // or a mode switch) — the attached properties default to empty themselves, but nothing else resets a value
    // this panel once pushed away from it. An empty focus box is what `MiniatureHost.Fit` reads as "not scaled".
    private static void _LeaveTheRail(Control tile)
    {
        SetMiniatureBox(tile, default, default);
        SetMiniatureFocusChildBox(tile, default);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Handled || !FocusRailLayout || _focusRailLayout is not { } layout || layout.Rail.Count == 0)
        {
            return;
        }

        var maxScroll = Math.Max(0, layout.Geometry.ContentHeight - Bounds.Height);
        if (maxScroll <= 0)
        {
            return;
        }

        _railScrollOffset = Math.Clamp(_railScrollOffset - (e.Delta.Y * RailScrollStep), 0, maxScroll);
        InvalidateArrange();
        e.Handled = true;
    }

    // AC-1013 (AC-444 #5): rail's spatial-nav counterpart to `NeighbourCell` — Right from focus enters the
    // rail's first tile, Left from its first column returns to focus, other directions walk row-major.
    private object? _NeighbourInFocusRail(object active, PaneDirection direction)
    {
        if (_focusRailLayout is not { } layout)
        {
            return null;
        }

        if (ReferenceEquals(layout.Focus.DataContext, active))
        {
            return direction == PaneDirection.Right && layout.Rail.Count > 0 ? layout.Rail[0].DataContext : null;
        }

        var index = -1;
        for (var i = 0; i < layout.Rail.Count; i++)
        {
            if (ReferenceEquals(layout.Rail[i].DataContext, active))
            {
                index = i;
                break;
            }
        }

        if (index < 0 || layout.Geometry.Columns <= 0)
        {
            return null;
        }

        var columns = layout.Geometry.Columns;
        var col = index % columns;
        var row = index / columns;

        return direction switch
        {
            PaneDirection.Left when col == 0 => layout.Focus.DataContext,
            PaneDirection.Left => _RailTileAt(layout, col - 1, row, columns),
            PaneDirection.Right => _RailTileAt(layout, col + 1, row, columns),
            PaneDirection.Up => _RailTileAt(layout, col, row - 1, columns),
            _ => _RailTileAt(layout, col, row + 1, columns),
        };
    }

    private static object? _RailTileAt(FocusRailLayoutResult layout, int col, int row, int columns)
    {
        if (col < 0 || row < 0 || col >= columns)
        {
            return null;
        }

        var index = (row * columns) + col;
        return index >= 0 && index < layout.Rail.Count ? layout.Rail[index].DataContext : null;
    }

    // Pure cell placement: moves `dragged` to `cell` within
    // `cells`, swapping with whatever is there (a hole leaves a hole behind), padding with
    // holes to reach the cell, then trimming trailing holes. Returns whether anything changed.
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

    // AC-1013: removes closed panes' cells and compacts survivors to the minimal grid (a closed tile is
    // gone, not a hole — two left of a 2×2 fall back to 1×2/2×1) while keeping deliberate free-placement
    // holes. `live` is the set of panes still present; returns whether any cell was removed.
    internal static bool DropClosedCells(List<object?> cells, IReadOnlySet<object> live)
    {
        var removed = false;
        for (var i = cells.Count - 1; i >= 0; i--)
        {
            if (cells[i] is { } key && !live.Contains(key))
            {
                cells.RemoveAt(i);
                removed = true;
            }
        }

        return removed;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        // No column/row gutters in the rail (AC-444 #1). ponytail: RailWeight has no live drag yet; wire one
        // (FocusRailPanel's own divider already proves the math) if operators want to hand-tune the split.
        if (FocusRailLayout)
        {
            return;
        }

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
            || FocusRailLayout
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

    // The vertical-gutter index (between two columns) under `x`, or -1. Only present with 2+ columns.
    private int ColumnGutterAt(double x)
    {
        var grid = GridSlots(Bounds.Width, Bounds.Height);
        return grid.Columns < 2 ? -1 : StackPaneMath.GutterAt(grid.Cols, x, Gutter, GrabTolerance);
    }

    // The horizontal-gutter index (between two rows) under `y`, or -1. Only present with 2+ rows.
    private int RowGutterAt(double y)
    {
        var grid = GridSlots(Bounds.Width, Bounds.Height);
        return grid.Rows.Count < 2 ? -1 : StackPaneMath.GutterAt(grid.Rows, y, Gutter, GrabTolerance);
    }

    // Column/row slot geometry for the current cell count, reconciling the positional weight arrays to the current dimensions.
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

    // Maps a cell index to its (column, row) for the current fill order: column-major when stacking
    // vertically (a column fills top-to-bottom before the next starts), row-major otherwise.
    private (int Col, int Row) CellOf(int index, int columns, int rows) =>
        CellCoords(index, columns, rows, StackVertically);

    // The static core of `CellOf`: (column, row) for a cell index in the given fill order.
    private static (int Col, int Row) CellCoords(int index, int columns, int rows, bool stackVertically)
    {
        var span = rows < 1 ? 1 : rows;
        var cols = columns < 1 ? 1 : columns;
        return stackVertically ? (index / span, index % span) : (index % cols, index / cols);
    }

    // Inverse of `CellOf`: the cell index at a given column/row.
    private int LinearOf(int col, int row, int columns, int rows) =>
        LinearIndex(col, row, columns, rows, StackVertically);

    // The static core of `LinearOf`: the cell index at a given column/row in the given fill order.
    private static int LinearIndex(int col, int row, int columns, int rows, bool stackVertically) =>
        stackVertically ? col * rows + row : row * columns + col;

    // Reconciles the cell list with this desk's panes: drops the cells of the ones that are gone (compacting the
    // rest — see `DropClosedCells`), gives each new pane the first hole or a trailing cell. Runs off every child
    // the active desk owns, zoom-collapsed included, so a placement survives single-pane; off no other desk's.
    private void ReconcileCells()
    {
        var live = new HashSet<object>();
        foreach (var child in Children)
        {
            if (LaysOutHere(child) && child.DataContext is { } key)
            {
                live.Add(key);
            }
        }

        DropClosedCells(_cells, live);

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
            if (LaysOutHere(child) && child.DataContext is { } key && present.Add(key))
            {
                PlaceInFirstHole(key);
            }
        }

        TrimTrailingHoles(_cells);
    }

    // A pane this grid owes a cell: one the active desk holds — hidden by zoom or not — plus anything actually
    // on screen, since a visible pane without a cell would never be arranged at all.
    private static bool LaysOutHere(Control child) => GetIsOnActiveDesk(child) || child.IsVisible;

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

    // Pads with equal 1.0 weights or trims extras so the positional weight list matches the axis's current length.
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

    // AC-1013: columns/rows for a cell count. One fills; two+ cap at two columns, growing downward
    // (3-4 -> 2x2). `stackVertically` transposes: caps at two rows, grows sideways. Fill order in `CellOf`.
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

    // AC-1013: the cell adjacent to `fromCell` in `direction`, or null at a grid edge. Walks cell by cell,
    // skipping holes, and returns the first occupied one — the nearest actual pane, never an empty slot.
    internal static int? NeighbourCell(IReadOnlyList<bool> occupied, int fromCell, PaneDirection direction, bool stackVertically)
    {
        var count = occupied.Count;
        if (fromCell < 0 || fromCell >= count)
        {
            return null;
        }

        var (columns, rows) = Dimensions(count, stackVertically);
        var (col, row) = CellCoords(fromCell, columns, rows, stackVertically);
        var (stepCol, stepRow) = direction switch
        {
            PaneDirection.Left => (-1, 0),
            PaneDirection.Right => (1, 0),
            PaneDirection.Up => (0, -1),
            _ => (0, 1),
        };

        for (int c = col + stepCol, r = row + stepRow;
             c >= 0 && c < columns && r >= 0 && r < rows;
             c += stepCol, r += stepRow)
        {
            var index = LinearIndex(c, r, columns, rows, stackVertically);
            if (index >= 0 && index < count && occupied[index])
            {
                return index;
            }
        }

        return null;
    }
}
