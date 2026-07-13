using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Media;

namespace OwnCanvasSpike;

/// <summary>
/// A flow canvas written from scratch on plain Avalonia — the question this spike answers is how much of
/// n8n's canvas we would actually have to build ourselves, now that no node-editor library works on Avalonia 12.
/// <para>
/// The answer, in this file: less than feared, because Avalonia already does the expensive parts. Nodes are
/// ordinary controls on a <see cref="Canvas"/>, so hit-testing, focus and input routing are the framework's
/// problem, not ours. What is genuinely ours: dragging a node (three pointer events), drawing a connection as a
/// bezier that follows the nodes it joins, dragging a new connection out of a pin, and pan/zoom (one
/// <see cref="RenderTransform"/>). That is what this file is, and it is ~200 lines.
/// </para>
/// What it deliberately does NOT do: undo, multi-select, snapping, routing around nodes, minimap. Those are real
/// work — but they are also not what makes a workflow tool useful on day one.
/// </summary>
internal sealed class FlowCanvas : Border
{
    private readonly Canvas _surface = new() { Background = Brushes.Transparent };
    private readonly ScaleTransform _zoom = new(1, 1);
    private readonly TranslateTransform _pan = new();

    private readonly List<FlowNode> _nodes = [];
    private readonly List<FlowConnection> _connections = [];

    private FlowNode? _draggingNode;
    private Point _dragOffset;

    private FlowPin? _pendingSource;
    private Path? _pendingLine;

    private bool _isPanning;
    private Point _panOrigin;

    public FlowCanvas()
    {
        Background = new SolidColorBrush(Color.Parse("#1B1B1F"));
        ClipToBounds = true;

        _surface.RenderTransform = new TransformGroup { Children = { _zoom, _pan } };
        _surface.RenderTransformOrigin = RelativePoint.TopLeft;
        Child = _surface;

        PointerPressed += _OnPointerPressed;
        PointerMoved += _OnPointerMoved;
        PointerReleased += _OnPointerReleased;
        PointerWheelChanged += _OnWheel;
    }

    public FlowNode AddNode(string title, double x, double y, int inputs, int outputs)
    {
        var node = new FlowNode(title, inputs, outputs);
        Canvas.SetLeft(node, x);
        Canvas.SetTop(node, y);

        node.HeaderPressed += (_, e) => _BeginNodeDrag(node, e);
        node.PinPressed += (_, pin) => _BeginConnection(pin);
        node.PinReleased += (_, pin) => _CompleteConnection(pin);

        _nodes.Add(node);
        _surface.Children.Add(node);
        return node;
    }

    public void Connect(FlowPin from, FlowPin to)
    {
        var connection = new FlowConnection(from, to);
        _connections.Add(connection);

        // Behind the nodes, so a curve never covers the thing it connects.
        _surface.Children.Insert(0, connection.Line);
        connection.Redraw();
    }

    public string Describe() => $"{_nodes.Count} nodes · {_connections.Count} connections · zoom {_zoom.ScaleX:0.00}";

    private void _BeginNodeDrag(FlowNode node, PointerPressedEventArgs e)
    {
        _draggingNode = node;
        _dragOffset = e.GetPosition(_surface) - new Point(Canvas.GetLeft(node), Canvas.GetTop(node));
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void _BeginConnection(FlowPin pin)
    {
        _pendingSource = pin;
        _pendingLine = FlowConnection.NewLine();
        _surface.Children.Insert(0, _pendingLine);
    }

    private void _CompleteConnection(FlowPin pin)
    {
        if (_pendingSource is { } source && !ReferenceEquals(source.Owner, pin.Owner))
        {
            Connect(source, pin);
        }

        _ClearPendingConnection();
    }

    private void _ClearPendingConnection()
    {
        if (_pendingLine is not null)
        {
            _surface.Children.Remove(_pendingLine);
        }

        _pendingLine = null;
        _pendingSource = null;
    }

    private void _OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Pressing the empty canvas pans it — the same gesture every canvas tool has.
        if (e.Source == _surface || e.Source == this)
        {
            _isPanning = true;
            _panOrigin = e.GetPosition(this);
            e.Pointer.Capture(this);
        }
    }

    private void _OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var onSurface = e.GetPosition(_surface);

        if (_draggingNode is { } node)
        {
            Canvas.SetLeft(node, onSurface.X - _dragOffset.X);
            Canvas.SetTop(node, onSurface.Y - _dragOffset.Y);
            _RedrawConnections(node);
            return;
        }

        if (_pendingSource is { } source && _pendingLine is { } line)
        {
            FlowConnection.Draw(line, source.AnchorOn(_surface), onSurface);
            return;
        }

        if (_isPanning)
        {
            var now = e.GetPosition(this);
            _pan.X += now.X - _panOrigin.X;
            _pan.Y += now.Y - _panOrigin.Y;
            _panOrigin = now;
        }
    }

    private void _OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // A connection dragged into empty space is not a connection.
        if (_pendingSource is not null)
        {
            _ClearPendingConnection();
        }

        _draggingNode = null;
        _isPanning = false;
        e.Pointer.Capture(null);
    }

    private void _OnWheel(object? sender, PointerWheelEventArgs e)
    {
        var factor = e.Delta.Y > 0 ? 1.1 : 1 / 1.1;
        var next = Math.Clamp(_zoom.ScaleX * factor, 0.3, 3.0);

        // Zoom towards the pointer, not the corner: anything else feels like the canvas is running away.
        var before = e.GetPosition(_surface);
        _zoom.ScaleX = _zoom.ScaleY = next;
        var after = e.GetPosition(_surface);

        _pan.X += (after.X - before.X) * next;
        _pan.Y += (after.Y - before.Y) * next;
        e.Handled = true;
    }

    private void _RedrawConnections(FlowNode moved)
    {
        foreach (var connection in _connections.Where(c => c.Touches(moved)))
        {
            connection.Redraw();
        }
    }
}
