using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Cockpit.Plugin.Diagram.Whiteboard.Model;

namespace Cockpit.Plugin.Diagram.Whiteboard.Canvas;

public enum WhiteboardTool
{
    Select,
    Pencil,
    PlaceShape,
}

// The whiteboard's surface (AC-821): a pencil draws yellow freehand strokes straight onto one shared layer, a
// shape tool drags out a blue-strict PlacedObject, Ctrl+V pastes a screenshot as the same kind of object, and
// Select drags bodies or resize-handle corners. No pan/zoom — nothing in the ticket asked for an infinite canvas.
public sealed class WhiteboardCanvasControl : Border
{
    private const double MinPlacedSize = 20;
    private const double HitTolerance = 6;

    private readonly Avalonia.Controls.Canvas _surface = new() { Background = Brushes.Transparent };
    private readonly FreehandLayer _freehandLayer;
    private readonly Dictionary<Guid, PlacedObjectControl> _placedControls = [];
    private readonly List<ResizeHandle> _handles = [];

    private List<WhiteboardPoint>? _activeStroke;

    private Point? _shapeStartPoint;
    private PlacedObjectControl? _shapeInProgress;

    private PlacedObjectControl? _draggingPlaced;
    private Point _dragOffset;

    private (HandleCorner Corner, PlacedObjectControl Control)? _resizing;
    private Rect _resizeStartBounds;
    private Point _resizeStartPointer;

    private Guid? _selectedId;
    private PlacedShapeKind _pendingShapeKind = PlacedShapeKind.Rectangle;

    public WhiteboardCanvasControl(WhiteboardDocument document)
    {
        Document = document;
        Background = Brushes.White;
        ClipToBounds = true;
        Focusable = true;

        _freehandLayer = new FreehandLayer(document);
        _surface.Children.Add(_freehandLayer);
        Avalonia.Controls.Canvas.SetLeft(_freehandLayer, 0);
        Avalonia.Controls.Canvas.SetTop(_freehandLayer, 0);

        SizeChanged += (_, e) =>
        {
            _freehandLayer.Width = e.NewSize.Width;
            _freehandLayer.Height = e.NewSize.Height;
        };

        Child = _surface;

        foreach (var placed in document.Objects.OfType<PlacedObject>())
        {
            _CreatePlacedControl(placed);
        }
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

    public void UseShapeTool(PlacedShapeKind kind)
    {
        _pendingShapeKind = kind;
        _SetTool(WhiteboardTool.PlaceShape);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        var point = e.GetPosition(_surface);

        if (Tool == WhiteboardTool.Pencil)
        {
            _activeStroke = [new WhiteboardPoint(point.X, point.Y)];
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (Tool == WhiteboardTool.PlaceShape)
        {
            _shapeStartPoint = point;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (_ControlOf(e.Source as Visual) is { } control)
        {
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

        if (_shapeStartPoint is { } start)
        {
            var rect = new Rect(start, point);
            if (_shapeInProgress is null)
            {
                var placed = new PlacedObject { ShapeKind = _pendingShapeKind, X = rect.X, Y = rect.Y, Width = 1, Height = 1 };
                Document.Add(placed);
                _shapeInProgress = _CreatePlacedControl(placed);
            }

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
                Document.Add(new FreehandStroke { Points = stroke });
                _freehandLayer.InvalidateVisual();
                Changed?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        if (_shapeInProgress is { } shape)
        {
            // A click with no drag places the default size centred on the click, rather than a sliver nobody could
            // grab a handle on.
            if (shape.Model.Width < 4 && shape.Model.Height < 4)
            {
                shape.Model.X -= 60;
                shape.Model.Y -= 40;
                shape.Model.Width = 120;
                shape.Model.Height = 80;
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

        if (_draggingPlaced is not null)
        {
            _draggingPlaced = null;
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

                var placed = new PlacedObject
                {
                    ShapeKind = PlacedShapeKind.Image,
                    X = 40,
                    Y = 40,
                    Width = Math.Min(bitmap.PixelSize.Width, 480),
                    Height = Math.Min(bitmap.PixelSize.Height, 360),
                    ImageData = stream.ToArray(),
                };

                Document.Add(placed);
                _CreatePlacedControl(placed);
                _Select(placed.Id);
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception)
        {
            // Clipboard unavailable (locked by another app, unsupported content): drop the paste, don't crash the UI thread.
        }
    }

    private PlacedObjectControl _CreatePlacedControl(PlacedObject placed)
    {
        var control = new PlacedObjectControl(placed);
        _placedControls[placed.Id] = control;
        _surface.Children.Add(control);
        _PositionPlaced(control);
        return control;
    }

    private void _RemoveObject(Guid id)
    {
        Document.Remove(id);

        if (_placedControls.Remove(id, out var control))
        {
            _surface.Children.Remove(control);
        }

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
