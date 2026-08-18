using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Cockpit.Plugin.Diagram.Whiteboard.Model;

namespace Cockpit.Plugin.Diagram.Whiteboard.Canvas;

public enum WhiteboardTool
{
    Select,
    Pencil,
    Marker,
    PlaceShape,
}

// The whiteboard's surface (AC-821): a pencil draws yellow freehand strokes straight onto one shared layer, a
// shape tool drags out a blue-strict PlacedObject, and Select drags bodies or resize-handle corners. No pan/zoom
// — nothing in the ticket asked for an infinite canvas. Ctrl+C/X/V/D (AC-917) run against an internal clipboard.
public sealed class WhiteboardCanvasControl : Border
{
    private const double MinPlacedSize = 20;
    private const double HitTolerance = 6;
    private const double ClickTolerance = 3;
    private const double PencilThickness = 2.5;
    private const double MarkerThickness = 14;

    private readonly Avalonia.Controls.Canvas _surface = new() { Background = Brushes.Transparent };
    private readonly FreehandLayer _freehandLayer;
    private readonly EmptyStateOverlay _emptyState = new() { IsHitTestVisible = false };
    private readonly Dictionary<Guid, PlacedObjectControl> _placedControls = [];
    private readonly List<ResizeHandle> _handles = [];

    private List<WhiteboardPoint>? _activeStroke;
    private bool _activeStrokeIsMarker;

    private Point? _shapeStartPoint;
    private PlacedObjectControl? _shapeInProgress;

    private PlacedObjectControl? _draggingPlaced;
    private Point _dragOffset;
    private Point? _pointerDownPoint;
    private bool _clickCandidateForEdit;
    private Rect _dragStartBounds;
    private IReadOnlyList<Guid> _dragCarried = [];

    private (HandleCorner Corner, PlacedObjectControl Control)? _resizing;
    private Rect _resizeStartBounds;
    private Point _resizeStartPointer;
    private IReadOnlyList<Guid> _resizeCarried = [];

    private TextBox? _activeEditor;
    private PlacedObjectControl? _editingControl;
    private string? _textBeforeEdit;

    private Border? _deleteImagePrompt;

    private Guid? _selectedId;
    private PlacedShapeKind _pendingShapeKind = PlacedShapeKind.Rectangle;

    // AC-917: a clipboard local to this board, not the system one — that one is already spoken for by the
    // image-from-system-clipboard paste below. Filled by Ctrl+C/Ctrl+X, read by Ctrl+V.
    private List<WhiteboardObject>? _clipboard;
    private double _pasteOffset;

    public WhiteboardCanvasControl(WhiteboardDocument document)
    {
        Document = document;
        Edits = new WhiteboardEditJournal(document.Id);
        Background = Brushes.White;
        ClipToBounds = true;
        Focusable = true;

        _surface.Children.Add(_emptyState);
        Avalonia.Controls.Canvas.SetLeft(_emptyState, 0);
        Avalonia.Controls.Canvas.SetTop(_emptyState, 0);

        _freehandLayer = new FreehandLayer(document);
        _surface.Children.Add(_freehandLayer);
        Avalonia.Controls.Canvas.SetLeft(_freehandLayer, 0);
        Avalonia.Controls.Canvas.SetTop(_freehandLayer, 0);

        SizeChanged += (_, e) =>
        {
            _freehandLayer.Width = e.NewSize.Width;
            _freehandLayer.Height = e.NewSize.Height;
            _emptyState.Width = e.NewSize.Width;
            _emptyState.Height = e.NewSize.Height;
        };

        Child = _surface;

        foreach (var placed in document.Objects.OfType<PlacedObject>())
        {
            _CreatePlacedControl(placed);
        }

        // Objects can also arrive from outside this control — an agent placing one over MCP (AC-854) — so controls
        // are kept in step with the document itself rather than only with the pointer gestures that make them.
        document.Objects.CollectionChanged += (_, change) =>
        {
            foreach (var placed in change.NewItems?.OfType<PlacedObject>() ?? [])
            {
                _CreatePlacedControl(placed);
            }

            foreach (var placed in change.OldItems?.OfType<PlacedObject>() ?? [])
            {
                _DropPlacedControl(placed.Id);
            }

            _freehandLayer.InvalidateVisual();
            _UpdateEmptyState();
        };
        _UpdateEmptyState();
    }

    public WhiteboardDocument Document { get; }

    // AC-912: the operator's own handlings, so Ctrl+Z here and the activity strip's "Terugdraaien" reach the same
    // inverses — the agent's half of that journal stays in IWhiteboardAccessRegistry.
    internal WhiteboardEditJournal Edits { get; }

    public WhiteboardTool Tool { get; private set; } = WhiteboardTool.Select;

    public Guid? SelectedId => _selectedId;

    // A gesture that has not ended has nothing journaled yet, so Ctrl+Z would have to reach back past it (AC-912 §7).
    private bool _GestureInProgress =>
        _activeStroke is not null || _shapeInProgress is not null || _draggingPlaced is not null || _resizing is not null || _activeEditor is not null;

    // Raised whenever the document changed (a stroke drawn, an object moved/resized/deleted, a paste) — the cue to save.
    public event EventHandler? Changed;

    // AC-912: why an undo or redo did nothing — the board refuses rather than silently touching work that landed
    // since, and the window this canvas sits in turns the reason into a toast.
    public event Action<string>? UndoRefused;

    public event EventHandler? SelectionChanged;

    // Raised after the tool switches, including the automatic switch back to Select once a shape is placed.
    public event EventHandler? ToolChanged;

    // AC-848: a click on an activity-strip line jumps to the object it named — the same select-and-show-handles as
    // a click on the canvas itself, just triggered from off-canvas. A no-op when the object is gone (erased since).
    public void SelectObject(Guid id)
    {
        if (Document.Find(id) is not null)
        {
            UseSelectTool();
            _Select(id);
        }
    }

    public void UseSelectTool() => _SetTool(WhiteboardTool.Select);

    public void UsePencilTool() => _SetTool(WhiteboardTool.Pencil);

    public void UseMarkerTool() => _SetTool(WhiteboardTool.Marker);

    public void UseShapeTool(PlacedShapeKind kind)
    {
        _pendingShapeKind = kind;
        _SetTool(WhiteboardTool.PlaceShape);
    }

    public Task PasteScreenshotAsync() => _PasteAsync();

    public async Task InsertImageAsync()
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null)
        {
            return;
        }

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Afbeelding invoegen",
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });

        var file = files.FirstOrDefault();
        if (file is null)
        {
            return;
        }

        try
        {
            await using var stream = await file.OpenReadAsync();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            var data = buffer.ToArray();

            using var bitmap = new Bitmap(new MemoryStream(data));
            _AddImageObject(data, bitmap.PixelSize.Width, bitmap.PixelSize.Height, isPastedScreenshot: false);
        }
        catch (Exception)
        {
            // Unreadable or unsupported file: drop the insert, don't crash the UI thread.
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        var point = e.GetPosition(_surface);

        if (Tool is WhiteboardTool.Pencil or WhiteboardTool.Marker)
        {
            _activeStroke = [new WhiteboardPoint(point.X, point.Y)];
            _activeStrokeIsMarker = Tool == WhiteboardTool.Marker;
            _freehandLayer.ActiveStroke = new FreehandLayer.DraftStroke(
                _activeStroke, _activeStrokeIsMarker ? MarkerThickness : PencilThickness, _activeStrokeIsMarker);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (Tool == WhiteboardTool.PlaceShape)
        {
            // Created right here, not on the first PointerMoved — a click with no movement at all must still place
            // something, per "neerzetten, niet tekenen" (#W2), rather than silently doing nothing. Lives only on
            // the surface until release — Document.Add happens there, so read_whiteboard never sees a mid-drag sliver.
            _shapeStartPoint = point;
            var placed = new PlacedObject { ShapeKind = _pendingShapeKind, X = point.X, Y = point.Y, Width = 1, Height = 1 };
            _shapeInProgress = _CreatePlacedControl(placed);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (_ControlOf(e.Source as Visual) is { } control)
        {
            // A second press on an already-selected, non-image object opens the text editor — but only once the
            // release proves it was a click and not the start of a drag (see OnPointerReleased).
            _clickCandidateForEdit = control.Model.ShapeKind != PlacedShapeKind.Image && _selectedId == control.Model.Id;
            _pointerDownPoint = point;

            _Select(control.Model.Id);
            _draggingPlaced = control;
            _dragStartBounds = _BoundsOf(control.Model);
            _dragCarried = _CarriedChildren(control.Model);
            _dragOffset = point - new Point(control.Model.X, control.Model.Y);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (_FreehandAt(point) is { } stroke)
        {
            _Select(stroke.Id);
            e.Handled = true;
            return;
        }

        _Select(null);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(_surface);

        if (_activeStroke is { } stroke)
        {
            stroke.Add(new WhiteboardPoint(point.X, point.Y));
            _freehandLayer.InvalidateVisual();
            return;
        }

        if (_shapeStartPoint is { } start && _shapeInProgress is not null)
        {
            var rect = new Rect(start, point);
            _shapeInProgress.Model.X = rect.X;
            _shapeInProgress.Model.Y = rect.Y;
            _shapeInProgress.Model.Width = Math.Max(1, rect.Width);
            _shapeInProgress.Model.Height = Math.Max(1, rect.Height);
            _PositionPlaced(_shapeInProgress);
            _shapeInProgress.Refresh();
            return;
        }

        if (_draggingPlaced is { } dragging)
        {
            var oldX = dragging.Model.X;
            var oldY = dragging.Model.Y;
            dragging.Model.X = point.X - _dragOffset.X;
            dragging.Model.Y = point.Y - _dragOffset.Y;

            // W-6/AC-851: dragging a pasted image carries whatever is anchored to it along by the same delta.
            if (dragging.Model.ShapeKind == PlacedShapeKind.Image)
            {
                WhiteboardBinding.CarryChildren(
                    Document, dragging.Model.Id,
                    oldX, oldY, dragging.Model.Width, dragging.Model.Height,
                    dragging.Model.X, dragging.Model.Y, dragging.Model.Width, dragging.Model.Height);
                _RefreshChildControls(dragging.Model.Id);
            }

            _PositionPlaced(dragging);
            _PositionHandlesFor(dragging);
            return;
        }

        if (_resizing is { } resize)
        {
            _ApplyResize(resize.Control, resize.Corner, point);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        // Releasing capture can synchronously raise OnPointerCaptureLost (Avalonia) — clear the draft state first
        // so that re-entrant call sees it already gone and no-ops, instead of racing the logic below.
        var stroke = _activeStroke;
        var shape = _shapeInProgress;
        _activeStroke = null;
        _freehandLayer.ActiveStroke = null;
        _shapeInProgress = null;
        _shapeStartPoint = null;
        e.Pointer.Capture(null);

        if (stroke is not null)
        {
            if (stroke.Count >= 2)
            {
                var isMarker = _activeStrokeIsMarker;
                var (centerX, centerY) = _BoundsCenter(stroke);
                var drawn = new FreehandStroke
                {
                    Points = stroke,
                    IsMarker = isMarker,
                    Thickness = isMarker ? MarkerThickness : PencilThickness,
                    ParentImageId = WhiteboardBinding.FindParentImage(Document, centerX, centerY)?.Id,
                };
                Document.Add(drawn);
                _JournalAdd(drawn, isMarker ? "drew a marker stroke" : "drew a pencil stroke");
                Changed?.Invoke(this, EventArgs.Empty);
            }

            _freehandLayer.InvalidateVisual();
            return;
        }

        if (shape is not null)
        {
            // A click with no drag places the default size centred on the click, rather than a sliver nobody could
            // grab a handle on — a sticky note gets a note-sized square, everything else the usual 120x80.
            if (shape.Model.Width < 4 && shape.Model.Height < 4)
            {
                var (width, height) = shape.Model.ShapeKind == PlacedShapeKind.StickyNote ? (140.0, 140.0) : (120.0, 80.0);
                shape.Model.X -= width / 2;
                shape.Model.Y -= height / 2;
                shape.Model.Width = width;
                shape.Model.Height = height;
                _PositionPlaced(shape);
                shape.Refresh();
            }

            // W-6/AC-851: a shape or label dropped over a pasted image belongs to it — same rule as a freehand
            // stroke's. Image itself is never a shape-tool choice, so this never binds an image to another image.
            shape.Model.ParentImageId = WhiteboardBinding.FindParentImage(
                Document, shape.Model.X + (shape.Model.Width / 2), shape.Model.Y + (shape.Model.Height / 2))?.Id;

            Document.Add(shape.Model);
            _Select(shape.Model.Id);
            UseSelectTool();
            _JournalAdd(shape.Model, $"placed a {shape.Model.ShapeKind}");
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_draggingPlaced is { } dragging)
        {
            var moved = _pointerDownPoint is { } start && _Distance(e.GetPosition(_surface), start) > ClickTolerance;
            _draggingPlaced = null;
            _pointerDownPoint = null;

            if (_clickCandidateForEdit && !moved)
            {
                _BeginTextEdit(dragging);
            }
            else if (moved)
            {
                _JournalBounds(dragging.Model, _dragStartBounds, _dragCarried, "moved");
            }

            _clickCandidateForEdit = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        if (_resizing is { } finished)
        {
            _resizing = null;
            if (_BoundsOf(finished.Control.Model) != _resizeStartBounds)
            {
                _JournalBounds(finished.Control.Model, _resizeStartBounds, _resizeCarried, "resized");
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // AC-898: window loses focus / alt-tab mid-gesture. Neither the draft stroke nor the shape being dragged out
    // ever reached Document, so undoing them here is just dropping the draft state, not a Document.Remove.
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);

        if (_activeStroke is not null)
        {
            _activeStroke = null;
            _freehandLayer.ActiveStroke = null;
            _freehandLayer.InvalidateVisual();
        }

        if (_shapeInProgress is { } shape)
        {
            _surface.Children.Remove(shape);
            _placedControls.Remove(shape.Model.Id);
            _shapeInProgress = null;
            _shapeStartPoint = null;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _CopySelection();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.X && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _CutSelection();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.D && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _DuplicateSelection();
            e.Handled = true;
            return;
        }

        // AC-917: a filled internal clipboard wins over the system one — the operator just copied or cut something
        // on this board, and that is what Ctrl+V most likely means. An empty internal clipboard falls through to
        // the pre-existing image-from-system-clipboard paste, so that flow keeps working untouched.
        if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (_clipboard is { Count: > 0 })
            {
                _PasteFromInternalClipboard();
            }
            else
            {
                _ = _PasteAsync();
            }

            e.Handled = true;
            return;
        }

        if (e.Key is Key.Z or Key.Y && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // AC-912 §7: mid-gesture nothing is journaled yet, so undo would have to reach back past the gesture and
            // leave it half-applied. The key does nothing instead; the gesture ends as the operator meant it to.
            if (_GestureInProgress)
            {
                return;
            }

            var redo = e.Key == Key.Y || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if ((redo ? Edits.RedoLast() : Edits.UndoLast()) is { } refusal)
            {
                UndoRefused?.Invoke(refusal);
            }

            e.Handled = true;
            return;
        }

        if (e.Key is Key.Delete or Key.Back && _selectedId is { } id)
        {
            _RequestRemove(id);
            e.Handled = true;
        }
    }

    // AC-917 AC1/AC2: snapshots the selection now — a clone with its own id, so later edits to the original never
    // leak into what a later Ctrl+V pastes. Copying an image takes its bound annotations along as one group, which
    // is what lets the eventual paste restore their binding inside the copy (AC4) instead of losing it.
    private void _CopySelection()
    {
        if (_GestureInProgress || _selectedId is not { } id || Document.Find(id) is not { } selected)
        {
            return;
        }

        _clipboard = [.. _BuildClipboardGroup(selected).Select(o => _Clone(o, o.Id))];
        _pasteOffset = 0;
    }

    // Same snapshot as copy, plus removing the selection from the board as one journaled handling (AC5) — cutting
    // an image takes its annotations with it rather than leaving them stuck to a picture that is no longer there.
    private void _CutSelection()
    {
        if (_GestureInProgress || _selectedId is not { } id || Document.Find(id) is not { } selected)
        {
            return;
        }

        var group = _BuildClipboardGroup(selected);
        _clipboard = [.. group.Select(o => _Clone(o, o.Id))];
        _pasteOffset = 0;

        var alsoRemoved = group.Where(o => o.Id != selected.Id).ToList();
        _DeleteJournaled(selected.Id, alsoRemoved, unbound: [], summaryOverride: _Describe(selected, "cut"));
    }

    // AC-917 AC6: the offset grows from the original by +10/+10 each time rather than resetting, so repeated
    // Ctrl+V fans copies out instead of stacking them on the exact same spot.
    private void _PasteFromInternalClipboard()
    {
        if (_clipboard is not { Count: > 0 } templates)
        {
            return;
        }

        _pasteOffset += 10;
        var clones = _Instantiate(templates, _pasteOffset, _pasteOffset);
        foreach (var clone in clones)
        {
            Document.Add(clone);
        }

        _JournalAddGroup(clones, _Describe(templates[0], "pasted"));
        _Select(clones[0].Id);
        UseSelectTool();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // Ctrl+D never touches _clipboard — duplicating a shape must not clobber whatever Ctrl+C put there for a later
    // Ctrl+V. The +10/+10 shift is the usual one so the copy is visibly beside the original, not hidden under it.
    private void _DuplicateSelection()
    {
        if (_GestureInProgress || _selectedId is not { } id || Document.Find(id) is not { } selected)
        {
            return;
        }

        var clones = _Instantiate(_BuildClipboardGroup(selected), 10, 10);
        foreach (var clone in clones)
        {
            Document.Add(clone);
        }

        _JournalAddGroup(clones, _Describe(selected, "duplicated"));
        _Select(clones[0].Id);
        UseSelectTool();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // W-6/AC-851: copying/cutting/duplicating an Image takes its bound children along as one group, so the binding
    // survives the copy intact (AC4). Anything else is a group of one.
    private List<WhiteboardObject> _BuildClipboardGroup(WhiteboardObject selected) =>
        selected is PlacedObject { ShapeKind: PlacedShapeKind.Image } image
            ? [selected, .. WhiteboardBinding.ChildrenOf(Document, image.Id)]
            : [selected];

    // New ids throughout (AC2). ParentImageId is rebuilt, not copied: inside the group it re-targets the cloned
    // parent (AC4); outside it, it falls back to whatever image now sits under the clone's position — the same
    // rule a freshly drawn stroke or shape already gets — so a lone annotation's copy never dangles (AC4).
    private List<WhiteboardObject> _Instantiate(IReadOnlyList<WhiteboardObject> templates, double dx, double dy)
    {
        var idMap = templates.ToDictionary(t => t.Id, _ => Guid.NewGuid());
        var clones = templates.Select(t => _Clone(t, idMap[t.Id])).ToList();

        foreach (var clone in clones)
        {
            _Translate(clone, dx, dy);
        }

        foreach (var (template, clone) in templates.Zip(clones))
        {
            if (template.ParentImageId is not { } parentId)
            {
                continue;
            }

            clone.ParentImageId = idMap.TryGetValue(parentId, out var newParentId)
                ? newParentId
                : WhiteboardBinding.FindParentImage(Document, _CenterOf(clone).X, _CenterOf(clone).Y)?.Id;
        }

        return clones;
    }

    private static WhiteboardObject _Clone(WhiteboardObject source, Guid id) => source switch
    {
        PlacedObject p => new PlacedObject
        {
            Id = id,
            ShapeKind = p.ShapeKind,
            X = p.X,
            Y = p.Y,
            Width = p.Width,
            Height = p.Height,
            Text = p.Text,
            ImageData = p.ImageData,
            IsPastedScreenshot = p.IsPastedScreenshot,
            ParentImageId = p.ParentImageId,
        },
        FreehandStroke f => new FreehandStroke
        {
            Id = id,
            Points = [.. f.Points],
            Thickness = f.Thickness,
            IsMarker = f.IsMarker,
            ParentImageId = f.ParentImageId,
        },
        _ => throw new NotSupportedException($"Unknown whiteboard object kind: {source.GetType()}"),
    };

    private static void _Translate(WhiteboardObject obj, double dx, double dy)
    {
        switch (obj)
        {
            case PlacedObject p:
                p.X += dx;
                p.Y += dy;
                break;
            case FreehandStroke f:
                for (var i = 0; i < f.Points.Count; i++)
                {
                    f.Points[i] = new WhiteboardPoint(f.Points[i].X + dx, f.Points[i].Y + dy);
                }

                break;
        }
    }

    private static (double X, double Y) _CenterOf(WhiteboardObject obj) => obj switch
    {
        PlacedObject p => (p.X + (p.Width / 2), p.Y + (p.Height / 2)),
        FreehandStroke f => _BoundsCenter(f.Points),
        _ => (0, 0),
    };

    private static string _Describe(WhiteboardObject obj, string verb) =>
        obj is PlacedObject placed ? $"{verb} a {placed.ShapeKind}" : $"{verb} a stroke";

    private async Task _PasteAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        try
        {
            var bitmap = await clipboard.TryGetBitmapAsync();
            if (bitmap is null)
            {
                return;
            }

            using (bitmap)
            {
                using var stream = new MemoryStream();
                bitmap.Save(stream, PngBitmapEncoderOptions.Default);
                _AddImageObject(stream.ToArray(), bitmap.PixelSize.Width, bitmap.PixelSize.Height, isPastedScreenshot: true);
            }
        }
        catch (Exception)
        {
            // Clipboard unavailable (locked by another app, unsupported content): drop the paste, don't crash the UI thread.
        }
    }

    private void _AddImageObject(byte[] data, int pixelWidth, int pixelHeight, bool isPastedScreenshot)
    {
        var placed = new PlacedObject
        {
            ShapeKind = PlacedShapeKind.Image,
            X = 40,
            Y = 40,
            Width = Math.Min(pixelWidth, 480),
            Height = Math.Min(pixelHeight, 360),
            ImageData = data,
            IsPastedScreenshot = isPastedScreenshot,
        };

        Document.Add(placed);
        _CreatePlacedControl(placed);
        _Select(placed.Id);
        _JournalAdd(placed, isPastedScreenshot ? "pasted a screenshot" : "inserted an image");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void _BeginTextEdit(PlacedObjectControl control)
    {
        _EndTextEdit(commit: true);
        _textBeforeEdit = control.Model.Text;

        var editor = new TextBox
        {
            Text = control.Model.Text ?? string.Empty,
            AcceptsReturn = true,
            Background = Brushes.White,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
        };
        editor.LostFocus += (_, _) => _EndTextEdit(commit: true);

        _surface.Children.Add(editor);
        Avalonia.Controls.Canvas.SetLeft(editor, control.Model.X);
        Avalonia.Controls.Canvas.SetTop(editor, control.Model.Y);
        editor.Width = control.Model.Width;
        editor.Height = control.Model.Height;

        _activeEditor = editor;
        _editingControl = control;
        editor.Focus();
        editor.SelectAll();
    }

    private void _EndTextEdit(bool commit)
    {
        if (_activeEditor is not { } editor || _editingControl is not { } control)
        {
            return;
        }

        _activeEditor = null;
        _editingControl = null;
        _surface.Children.Remove(editor);

        if (!commit)
        {
            return;
        }

        var before = _textBeforeEdit;
        var after = string.IsNullOrEmpty(editor.Text) ? null : editor.Text;
        control.Model.Text = after;
        control.Refresh();

        if (after != before)
        {
            var id = control.Model.Id;
            Edits.Record($"changed the text of a {control.Model.ShapeKind}", id, () => _ApplyText(id, before), () => _ApplyText(id, after));
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void _UpdateEmptyState()
    {
        _emptyState.IsVisible = Document.Objects.Count == 0;
    }

    // Paints straight into the live canvas, never the exported snapshot — the ticket points at this control's own
    // blank white background, not at what PNG the registry sees.
    internal sealed class EmptyStateOverlay : Control
    {
        private const double DotSpacing = 24;
        private const string Message = "Leeg bord. Teken, plak een screenshot, of zet een vorm neer.";

        private static readonly IBrush DotBrush = new SolidColorBrush(Color.Parse("#D6DEE8"));
        private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#94A3B8"));

        public override void Render(DrawingContext context)
        {
            var bounds = new Rect(Bounds.Size);

            for (var x = DotSpacing / 2; x < bounds.Width; x += DotSpacing)
            {
                for (var y = DotSpacing / 2; y < bounds.Height; y += DotSpacing)
                {
                    context.DrawEllipse(DotBrush, null, new Point(x, y), 1.5, 1.5);
                }
            }

            var formatted = new FormattedText(
                Message,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                Typeface.Default,
                14,
                TextBrush)
            {
                TextAlignment = TextAlignment.Center,
                MaxTextWidth = Math.Max(1, bounds.Width - 80),
            };

            context.DrawText(formatted, new Point((bounds.Width - formatted.Width) / 2, (bounds.Height - formatted.Height) / 2));
        }
    }

    private PlacedObjectControl _CreatePlacedControl(PlacedObject placed)
    {
        if (_placedControls.TryGetValue(placed.Id, out var existing))
        {
            return existing;
        }

        var control = new PlacedObjectControl(placed);
        _placedControls[placed.Id] = control;
        _surface.Children.Add(control);
        _PositionPlaced(control);
        return control;
    }

    private void _DropPlacedControl(Guid id)
    {
        if (_placedControls.Remove(id, out var control))
        {
            _surface.Children.Remove(control);
        }
    }

    // W-6/AC-851: deleting a pasted image that annotations are anchored to must ask what happens to them — never
    // silently drag them into deletion, never silently orphan them. Anything else deletes immediately, as before.
    private void _RequestRemove(Guid id)
    {
        if (Document.Find(id) is PlacedObject { ShapeKind: PlacedShapeKind.Image } &&
            WhiteboardBinding.ChildrenOf(Document, id) is { Count: > 0 } children &&
            _placedControls.TryGetValue(id, out var control))
        {
            _ShowDeleteImageMenu(id, children, control);
            return;
        }

        _DeleteJournaled(id, alsoRemoved: [], unbound: []);
    }

    // An inline panel dropped straight onto the surface — the same "no popup" approach _BeginTextEdit already uses
    // for its TextBox — rather than a MenuFlyout, which needs an overlay layer this borderless canvas doesn't own.
    private void _ShowDeleteImageMenu(Guid imageId, IReadOnlyList<WhiteboardObject> children, PlacedObjectControl anchor)
    {
        _DismissDeleteImagePrompt();

        var both = new Button { Content = $"Afbeelding en {children.Count} aantekening(en) verwijderen", Classes = { "Compact" } };
        both.Click += (_, _) =>
        {
            _DismissDeleteImagePrompt();
            _DeleteJournaled(imageId, alsoRemoved: children, unbound: []);
        };

        var detach = new Button { Content = "Alleen de afbeelding — aantekeningen loskoppelen", Classes = { "Compact" } };
        detach.Click += (_, _) =>
        {
            _DismissDeleteImagePrompt();
            _DeleteJournaled(imageId, alsoRemoved: [], unbound: children);
        };

        var cancel = new Button { Content = "Annuleren", Classes = { "Compact" } };
        cancel.Click += (_, _) => _DismissDeleteImagePrompt();

        var prompt = new Border
        {
            Background = Brushes.White,
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = "Wat moet er gebeuren met de aantekeningen op deze afbeelding?", TextWrapping = TextWrapping.Wrap, MaxWidth = 220 },
                    both,
                    detach,
                    cancel,
                },
            },
        };

        _deleteImagePrompt = prompt;
        _surface.Children.Add(prompt);
        Avalonia.Controls.Canvas.SetLeft(prompt, anchor.Model.X);
        Avalonia.Controls.Canvas.SetTop(prompt, anchor.Model.Y);
    }

    private void _DismissDeleteImagePrompt()
    {
        if (_deleteImagePrompt is { } prompt)
        {
            _surface.Children.Remove(prompt);
            _deleteImagePrompt = null;
        }
    }

    // Repositions every control belonging to `parentId`'s children after WhiteboardBinding.CarryChildren mutated
    // their model geometry — placed children need their canvas position + size, freehand children only a repaint.
    private void _RefreshChildControls(Guid parentId)
    {
        foreach (var child in Document.Objects.Where(o => o.ParentImageId == parentId))
        {
            if (child is PlacedObject placedChild && _placedControls.TryGetValue(placedChild.Id, out var childControl))
            {
                _PositionPlaced(childControl);
                childControl.Refresh();
            }
        }

        _freehandLayer.InvalidateVisual();
    }

    private static (double X, double Y) _BoundsCenter(IReadOnlyList<WhiteboardPoint> points)
    {
        var minX = points.Min(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxX = points.Max(p => p.X);
        var maxY = points.Max(p => p.Y);
        return ((minX + maxX) / 2, (minY + maxY) / 2);
    }

    // AC-912: what one delete took off the board — the removed objects with the spot each sat in, plus the children
    // whose binding to `FocusId` was cut rather than deleted along ("Alleen de afbeelding").
    private sealed record Removal(IReadOnlyList<(WhiteboardObject Item, int Index)> Removed, IReadOnlyList<WhiteboardObject> Unbound, Guid FocusId);

    // The instances themselves are journaled, not a copy of the board: restoring one puts back the very object that
    // left, so it keeps the id AC-851's ParentImageId bindings point at.
    private void _DeleteJournaled(
        Guid id, IReadOnlyList<WhiteboardObject> alsoRemoved, IReadOnlyList<WhiteboardObject> unbound, string? summaryOverride = null)
    {
        if (Document.Find(id) is not { } focus)
        {
            return;
        }

        var removal = new Removal(
            [.. alsoRemoved.Append(focus).Select(item => (Item: item, Index: Document.Objects.IndexOf(item)))],
            unbound,
            id);

        Edits.Record(
            summaryOverride ?? (focus is PlacedObject placed ? $"deleted a {placed.ShapeKind}" : "erased a stroke"),
            id,
            () => _Restore(removal),
            () => _Drop(removal));
        _Drop(removal);
    }

    private string? _Drop(Removal removal)
    {
        foreach (var child in removal.Unbound)
        {
            child.ParentImageId = null;
        }

        foreach (var (item, _) in removal.Removed)
        {
            Document.Remove(item.Id);
        }

        _ClearHandles();
        _selectedId = null;
        _ApplySelectedHighlight(null);
        _freehandLayer.SelectedId = null;
        _freehandLayer.InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
        return null;
    }

    // Back at its own index rather than on top of the stack: the document's order is what the PNG snapshot draws, so
    // an image restored at the end would cover the annotations that were made on it.
    private string? _Restore(Removal removal)
    {
        foreach (var (item, index) in removal.Removed.OrderBy(entry => entry.Index))
        {
            if (Document.Find(item.Id) is null)
            {
                Document.Objects.Insert(Math.Min(index, Document.Objects.Count), item);
            }
        }

        foreach (var child in removal.Unbound.Where(child => Document.Find(child.Id) is not null))
        {
            child.ParentImageId = removal.FocusId;
        }

        _freehandLayer.InvalidateVisual();
        Changed?.Invoke(this, EventArgs.Empty);
        return null;
    }

    private void _JournalAdd(WhiteboardObject added, string summary)
    {
        var removal = new Removal([(added, Document.Objects.IndexOf(added))], [], added.Id);
        Edits.Record(summary, added.Id, () => _UndoAdd(removal), () => _Restore(removal));
    }

    // AC-917: same contract as _JournalAdd — the caller has already added `objects` to the Document, this only
    // records the undo/redo pair. One journal line for the whole group (AC5), so cutting/pasting/duplicating an
    // image together with its annotations reverts as a single entry, not one per object.
    private void _JournalAddGroup(IReadOnlyList<WhiteboardObject> objects, string summary)
    {
        var removal = new Removal([.. objects.Select(o => (Item: o, Index: Document.Objects.IndexOf(o)))], [], objects[0].Id);
        Edits.Record(summary, removal.FocusId, () => _UndoAddGroup(removal), () => _Restore(removal));
    }

    // Same refusal as _UndoAdd, group-wide: anything anchored to a pasted/duplicated image since it landed — the
    // agent's included — blocks the undo instead of dragging that work away silently.
    private string? _UndoAddGroup(Removal removal)
    {
        var ownIds = removal.Removed.Select(entry => entry.Item.Id).ToHashSet();
        foreach (var (item, _) in removal.Removed)
        {
            if (item is PlacedObject { ShapeKind: PlacedShapeKind.Image } &&
                WhiteboardBinding.ChildrenOf(Document, item.Id).Any(child => !ownIds.Contains(child.Id)))
            {
                return "Er ligt werk op dit object — verwijder dat eerst.";
            }
        }

        return _Drop(removal);
    }

    // Refuses while something is anchored to the object: taking it away would drag along or unbind work that landed
    // on it since — the agent's included — and AC-912 asks for a refusal there, not a silent side effect.
    private string? _UndoAdd(Removal removal)
    {
        if (Document.Find(removal.FocusId) is null)
        {
            return "Dit object staat niet meer op het bord.";
        }

        return WhiteboardBinding.ChildrenOf(Document, removal.FocusId).Count > 0
            ? "Er ligt werk op dit object — verwijder dat eerst."
            : _Drop(removal);
    }

    private void _JournalBounds(PlacedObject placed, Rect before, IReadOnlyList<Guid> carried, string verb)
    {
        var after = _BoundsOf(placed);
        var id = placed.Id;
        Edits.Record($"{verb} a {placed.ShapeKind}", id, () => _ApplyBounds(id, before, carried), () => _ApplyBounds(id, after, carried));
    }

    // Carries only the children the gesture itself carried, so an annotation that landed on the image afterwards —
    // the agent's or the operator's — stays exactly where it is (AC-912's fifth criterion).
    private string? _ApplyBounds(Guid id, Rect bounds, IReadOnlyList<Guid> carried)
    {
        if (Document.Find(id) is not PlacedObject placed || !_placedControls.TryGetValue(id, out var control))
        {
            return "Dit object staat niet meer op het bord.";
        }

        if (carried.Count > 0)
        {
            WhiteboardBinding.CarryChildren(
                Document, id,
                placed.X, placed.Y, placed.Width, placed.Height,
                bounds.X, bounds.Y, bounds.Width, bounds.Height,
                carried);
            _RefreshChildControls(id);
        }

        placed.X = bounds.X;
        placed.Y = bounds.Y;
        placed.Width = bounds.Width;
        placed.Height = bounds.Height;
        _PositionPlaced(control);
        control.Refresh();

        if (_selectedId == id)
        {
            _PositionHandlesFor(control);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return null;
    }

    private string? _ApplyText(Guid id, string? text)
    {
        if (Document.Find(id) is not PlacedObject placed || !_placedControls.TryGetValue(id, out var control))
        {
            return "Dit object staat niet meer op het bord.";
        }

        placed.Text = text;
        control.Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
        return null;
    }

    private static Rect _BoundsOf(PlacedObject placed) => new(placed.X, placed.Y, placed.Width, placed.Height);

    private IReadOnlyList<Guid> _CarriedChildren(PlacedObject placed) =>
        placed.ShapeKind == PlacedShapeKind.Image ? [.. WhiteboardBinding.ChildrenOf(Document, placed.Id).Select(child => child.Id)] : [];

    private void _Select(Guid? id)
    {
        _selectedId = id;
        _ApplySelectedHighlight(id);

        var isFreehand = id is { } selected && Document.Find(selected) is FreehandStroke;
        _freehandLayer.SelectedId = isFreehand ? id : null;
        _freehandLayer.InvalidateVisual();

        _ClearHandles();
        if (!isFreehand && id is { } placedId && _placedControls.TryGetValue(placedId, out var control))
        {
            _CreateHandlesFor(control);
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // AC-918: the badge only shows while an object is selected or hovered — keep every control's flag in sync
    // with the one id the board considers selected, rather than trusting each call site to clear the last one.
    private void _ApplySelectedHighlight(Guid? id)
    {
        foreach (var (key, control) in _placedControls)
        {
            control.IsSelected = key == id;
        }
    }

    private void _SetTool(WhiteboardTool tool)
    {
        Tool = tool;
        ToolChanged?.Invoke(this, EventArgs.Empty);
    }

    private void _CreateHandlesFor(PlacedObjectControl control)
    {
        foreach (var corner in new[] { HandleCorner.TopLeft, HandleCorner.TopRight, HandleCorner.BottomLeft, HandleCorner.BottomRight })
        {
            var handle = new ResizeHandle(corner);
            handle.Pressed += (_, e) =>
            {
                _resizing = (corner, control);
                _resizeStartBounds = _BoundsOf(control.Model);
                _resizeCarried = _CarriedChildren(control.Model);
                _resizeStartPointer = e.GetPosition(_surface);
                e.Pointer.Capture(this);
            };
            _handles.Add(handle);
            _surface.Children.Add(handle);
        }

        _PositionHandlesFor(control);
    }

    private void _ClearHandles()
    {
        foreach (var handle in _handles)
        {
            _surface.Children.Remove(handle);
        }

        _handles.Clear();
    }

    private void _PositionPlaced(PlacedObjectControl control)
    {
        Avalonia.Controls.Canvas.SetLeft(control, control.Model.X);
        Avalonia.Controls.Canvas.SetTop(control, control.Model.Y);
        control.Width = control.Model.Width;
        control.Height = control.Model.Height;
    }

    private void _PositionHandlesFor(PlacedObjectControl control)
    {
        var rect = new Rect(control.Model.X, control.Model.Y, control.Model.Width, control.Model.Height);
        var corners = new Dictionary<HandleCorner, Point>
        {
            [HandleCorner.TopLeft] = rect.TopLeft,
            [HandleCorner.TopRight] = rect.TopRight,
            [HandleCorner.BottomLeft] = rect.BottomLeft,
            [HandleCorner.BottomRight] = rect.BottomRight,
        };

        foreach (var handle in _handles)
        {
            var corner = corners[handle.Corner];
            Avalonia.Controls.Canvas.SetLeft(handle, corner.X - 5);
            Avalonia.Controls.Canvas.SetTop(handle, corner.Y - 5);
        }
    }

    private void _ApplyResize(PlacedObjectControl control, HandleCorner corner, Point pointer)
    {
        var delta = pointer - _resizeStartPointer;
        var bounds = _resizeStartBounds;
        double x = bounds.X, y = bounds.Y, width = bounds.Width, height = bounds.Height;

        switch (corner)
        {
            case HandleCorner.TopLeft:
                x += delta.X; y += delta.Y; width -= delta.X; height -= delta.Y;
                break;
            case HandleCorner.TopRight:
                y += delta.Y; width += delta.X; height -= delta.Y;
                break;
            case HandleCorner.BottomLeft:
                x += delta.X; width -= delta.X; height += delta.Y;
                break;
            case HandleCorner.BottomRight:
                width += delta.X; height += delta.Y;
                break;
        }

        if (width < MinPlacedSize)
        {
            if (corner is HandleCorner.TopLeft or HandleCorner.BottomLeft)
            {
                x = bounds.X + bounds.Width - MinPlacedSize;
            }

            width = MinPlacedSize;
        }

        if (height < MinPlacedSize)
        {
            if (corner is HandleCorner.TopLeft or HandleCorner.TopRight)
            {
                y = bounds.Y + bounds.Height - MinPlacedSize;
            }

            height = MinPlacedSize;
        }

        // W-6/AC-851: scale/translate whatever is anchored to this image using the fixed start bounds, same
        // non-incremental basis _resizeStartBounds already gives the resize itself — no drift across pointer moves.
        if (control.Model.ShapeKind == PlacedShapeKind.Image)
        {
            WhiteboardBinding.CarryChildren(
                Document, control.Model.Id,
                bounds.X, bounds.Y, bounds.Width, bounds.Height,
                x, y, width, height);
            _RefreshChildControls(control.Model.Id);
        }

        control.Model.X = x;
        control.Model.Y = y;
        control.Model.Width = width;
        control.Model.Height = height;
        _PositionPlaced(control);
        control.Refresh();
        _PositionHandlesFor(control);
    }

    private PlacedObjectControl? _ControlOf(Visual? source)
    {
        while (source is not null)
        {
            if (source is PlacedObjectControl control)
            {
                return control;
            }

            source = source.GetVisualParent();
        }

        return null;
    }

    private FreehandStroke? _FreehandAt(Point point)
    {
        foreach (var stroke in Document.Objects.OfType<FreehandStroke>().Reverse())
        {
            for (var i = 1; i < stroke.Points.Count; i++)
            {
                var a = new Point(stroke.Points[i - 1].X, stroke.Points[i - 1].Y);
                var b = new Point(stroke.Points[i].X, stroke.Points[i].Y);
                if (_DistanceToSegment(point, a, b) <= HitTolerance)
                {
                    return stroke;
                }
            }
        }

        return null;
    }

    private static double _DistanceToSegment(Point p, Point a, Point b)
    {
        var abX = b.X - a.X;
        var abY = b.Y - a.Y;
        var lengthSquared = (abX * abX) + (abY * abY);
        if (lengthSquared < 1e-6)
        {
            return _Distance(p, a);
        }

        var t = Math.Clamp((((p.X - a.X) * abX) + ((p.Y - a.Y) * abY)) / lengthSquared, 0, 1);
        var projection = new Point(a.X + (abX * t), a.Y + (abY * t));
        return _Distance(p, projection);
    }

    private static double _Distance(Point a, Point b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
