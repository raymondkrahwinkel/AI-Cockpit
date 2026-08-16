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
// shape tool drags out a blue-strict PlacedObject, Ctrl+V pastes a screenshot as the same kind of object, and
// Select drags bodies or resize-handle corners. No pan/zoom — nothing in the ticket asked for an infinite canvas.
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

    private (HandleCorner Corner, PlacedObjectControl Control)? _resizing;
    private Rect _resizeStartBounds;
    private Point _resizeStartPointer;

    private TextBox? _activeEditor;
    private PlacedObjectControl? _editingControl;

    private Guid? _selectedId;
    private PlacedShapeKind _pendingShapeKind = PlacedShapeKind.Rectangle;

    public WhiteboardCanvasControl(WhiteboardDocument document)
    {
        Document = document;
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

    public WhiteboardTool Tool { get; private set; } = WhiteboardTool.Select;

    public Guid? SelectedId => _selectedId;

    // Raised whenever the document changed (a stroke drawn, an object moved/resized/deleted, a paste) — the cue to save.
    public event EventHandler? Changed;

    public event EventHandler? SelectionChanged;

    // Raised after the tool switches, including the automatic switch back to Select once a shape is placed.
    public event EventHandler? ToolChanged;

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
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (Tool == WhiteboardTool.PlaceShape)
        {
            // Created right here, not on the first PointerMoved — a click with no movement at all must still place
            // something, per "neerzetten, niet tekenen" (#W2), rather than silently doing nothing.
            _shapeStartPoint = point;
            var placed = new PlacedObject { ShapeKind = _pendingShapeKind, X = point.X, Y = point.Y, Width = 1, Height = 1 };
            Document.Add(placed);
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
            dragging.Model.X = point.X - _dragOffset.X;
            dragging.Model.Y = point.Y - _dragOffset.Y;
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
        e.Pointer.Capture(null);

        if (_activeStroke is { } stroke)
        {
            _activeStroke = null;
            if (stroke.Count >= 2)
            {
                var isMarker = _activeStrokeIsMarker;
                Document.Add(new FreehandStroke
                {
                    Points = stroke,
                    IsMarker = isMarker,
                    Thickness = isMarker ? MarkerThickness : PencilThickness,
                });
                _freehandLayer.InvalidateVisual();
                Changed?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        if (_shapeInProgress is { } shape)
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

            _shapeInProgress = null;
            _shapeStartPoint = null;
            _Select(shape.Model.Id);
            UseSelectTool();
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

            _clickCandidateForEdit = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        if (_resizing is not null)
        {
            _resizing = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _ = _PasteAsync();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Delete or Key.Back && _selectedId is { } id)
        {
            _RemoveObject(id);
            e.Handled = true;
        }
    }

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
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void _BeginTextEdit(PlacedObjectControl control)
    {
        _EndTextEdit(commit: true);

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

        if (commit)
        {
            control.Model.Text = string.IsNullOrEmpty(editor.Text) ? null : editor.Text;
            control.Refresh();
            Changed?.Invoke(this, EventArgs.Empty);
        }
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

    private void _RemoveObject(Guid id)
    {
        Document.Remove(id);
        _ClearHandles();
        _selectedId = null;
        _freehandLayer.SelectedId = null;
        _freehandLayer.InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void _Select(Guid? id)
    {
        _selectedId = id;

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
                _resizeStartBounds = new Rect(control.Model.X, control.Model.Y, control.Model.Width, control.Model.Height);
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
