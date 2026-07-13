using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Cockpit.Plugin.Workflows.Model;
using Path = Avalonia.Controls.Shapes.Path;

namespace Cockpit.Plugin.Workflows.Canvas;

/// <summary>
/// The flow editor's surface (#69): nodes you drag, pins you pull wires out of, pan and zoom. Written on plain
/// Avalonia rather than on a node-editor library because none of them run on Avalonia 12 — every one depends on
/// Avalonia.Xaml.Behaviors, which has no Avalonia 12 release (see the spike in spikes/spike-node-editor).
/// <para>
/// It renders a <see cref="Workflow"/> and writes straight back into it, so what you see and what gets saved
/// cannot drift apart. The rules about what may be wired to what belong to the model, not here — the canvas
/// merely asks, and reports the refusal.
/// </para>
/// </summary>
internal sealed class WorkflowCanvas : Border
{
    private const double MinZoom = 0.3;
    private const double MaxZoom = 3.0;

    private readonly Avalonia.Controls.Canvas _surface = new() { Background = Brushes.Transparent };
    private readonly ScaleTransform _zoom = new(1, 1);
    private readonly TranslateTransform _pan = new();

    private readonly Dictionary<string, WorkflowNodeControl> _nodes = new(StringComparer.Ordinal);
    private readonly List<WorkflowWire> _wires = [];

    private WorkflowNodeControl? _draggingNode;
    private Point _dragOffset;

    private WorkflowPin? _pendingSource;
    private Path? _pendingWire;

    private bool _isPanning;
    private Point _panOrigin;

    public WorkflowCanvas(Workflow workflow)
    {
        Workflow = workflow;

        Background = _Brush("CockpitSecondaryBgBrush") ?? new SolidColorBrush(Color.Parse("#1B1B1F"));
        ClipToBounds = true;
        Focusable = true;

        _surface.RenderTransform = new TransformGroup { Children = { _zoom, _pan } };
        _surface.RenderTransformOrigin = RelativePoint.TopLeft;
        Child = _surface;

        PointerPressed += _OnPointerPressed;
        PointerMoved += _OnPointerMoved;
        PointerReleased += _OnPointerReleased;
        PointerWheelChanged += _OnWheel;
        KeyDown += _OnKeyDown;

        Rebuild();
    }

    public Workflow Workflow { get; }

    /// <summary>Raised when the canvas changed the workflow (a node moved, a wire was drawn or something was deleted) — the cue to save.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when the model refused a wire, carrying the reason, so the dialog can say it out loud instead of the drag silently doing nothing.</summary>
    public event EventHandler<string>? Refused;

    /// <summary>The node the operator last clicked, or null — what a properties panel and the Delete key both act on.</summary>
    public WorkflowNode? Selected { get; private set; }

    public event EventHandler? SelectionChanged;

    public void Add(WorkflowNode node)
    {
        Workflow.Nodes.Add(node);
        _AddControl(node);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Rebuilds every control from the workflow — used on load, and after a change that moves more than one thing.</summary>
    public void Rebuild()
    {
        _surface.Children.Clear();
        _nodes.Clear();
        _wires.Clear();

        foreach (var node in Workflow.Nodes)
        {
            _AddControl(node);
        }

        foreach (var connection in Workflow.Connections)
        {
            _AddWire(connection);
        }
    }

    private void _AddControl(WorkflowNode node)
    {
        var control = new WorkflowNodeControl(node);
        Avalonia.Controls.Canvas.SetLeft(control, node.X);
        Avalonia.Controls.Canvas.SetTop(control, node.Y);

        control.HeaderPressed += (_, e) => _BeginNodeDrag(control, e);
        control.PinPressed += (_, pin) => _BeginWire(pin);
        control.PinReleased += (_, pin) => _CompleteWire(pin);

        _nodes[node.Id] = control;
        _surface.Children.Add(control);
    }

    private void _AddWire(WorkflowConnection connection)
    {
        if (!_nodes.TryGetValue(connection.FromNodeId, out var from) || !_nodes.TryGetValue(connection.ToNodeId, out var to))
        {
            return;
        }

        var wire = new WorkflowWire(connection, from.OutputPin(connection.FromOutput), to.InputPin());
        _wires.Add(wire);

        // Behind the nodes: a curve must never cover the thing it connects.
        _surface.Children.Insert(0, wire.Line);
        wire.Redraw(_surface);
    }

    private void _BeginNodeDrag(WorkflowNodeControl control, PointerPressedEventArgs e)
    {
        _Select(control.Node);
        _draggingNode = control;
        _dragOffset = e.GetPosition(_surface) - new Point(
            Avalonia.Controls.Canvas.GetLeft(control),
            Avalonia.Controls.Canvas.GetTop(control));
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void _BeginWire(WorkflowPin pin)
    {
        // Wires are pulled from an output; dropping onto an input is what finishes them.
        if (pin.IsInput)
        {
            return;
        }

        _pendingSource = pin;
        _pendingWire = WorkflowWire.NewLine();
        _surface.Children.Insert(0, _pendingWire);
    }

    private void _CompleteWire(WorkflowPin pin)
    {
        if (_pendingSource is { } source && pin.IsInput)
        {
            var rule = Workflow.Connect(source.Owner.Node.Id, source.OutputIndex, pin.Owner.Node.Id);
            if (rule.IsAllowed)
            {
                _AddWire(Workflow.Connections[^1]);
                Changed?.Invoke(this, EventArgs.Empty);
            }
            else if (rule.Reason is { } reason)
            {
                Refused?.Invoke(this, reason);
            }
        }

        _ClearPendingWire();
    }

    private void _ClearPendingWire()
    {
        if (_pendingWire is not null)
        {
            _surface.Children.Remove(_pendingWire);
        }

        _pendingWire = null;
        _pendingSource = null;
    }

    private void _Select(WorkflowNode? node)
    {
        Selected = node;
        foreach (var (id, control) in _nodes)
        {
            control.IsSelected = node is not null && id == node.Id;
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void _OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Focus();

        // Pressing the empty canvas pans it, and clears the selection: the same gesture every canvas tool has.
        if (ReferenceEquals(e.Source, _surface) || ReferenceEquals(e.Source, this))
        {
            _Select(null);
            _isPanning = true;
            _panOrigin = e.GetPosition(this);
            e.Pointer.Capture(this);
        }
    }

    private void _OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var onSurface = e.GetPosition(_surface);

        if (_draggingNode is { } control)
        {
            var x = onSurface.X - _dragOffset.X;
            var y = onSurface.Y - _dragOffset.Y;
            Avalonia.Controls.Canvas.SetLeft(control, x);
            Avalonia.Controls.Canvas.SetTop(control, y);
            control.Node.X = x;
            control.Node.Y = y;
            _RedrawWires(control.Node.Id);
            return;
        }

        if (_pendingSource is { } source && _pendingWire is { } line)
        {
            WorkflowWire.Draw(line, source.AnchorOn(_surface), onSurface);
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
        // A wire dropped in empty space is not a wire.
        if (_pendingSource is not null)
        {
            _ClearPendingWire();
        }

        if (_draggingNode is not null)
        {
            _draggingNode = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        _isPanning = false;
        e.Pointer.Capture(null);
    }

    private void _OnWheel(object? sender, PointerWheelEventArgs e)
    {
        var factor = e.Delta.Y > 0 ? 1.1 : 1 / 1.1;
        var next = Math.Clamp(_zoom.ScaleX * factor, MinZoom, MaxZoom);

        // Zoom towards the pointer, not the corner — anything else feels like the canvas is running away.
        var before = e.GetPosition(_surface);
        _zoom.ScaleX = _zoom.ScaleY = next;
        var after = e.GetPosition(_surface);

        _pan.X += (after.X - before.X) * next;
        _pan.Y += (after.Y - before.Y) * next;
        e.Handled = true;
    }

    private void _OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Delete or Key.Back) || Selected is not { } node)
        {
            return;
        }

        Workflow.Remove(node.Id);
        _Select(null);
        Rebuild();
        Changed?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void _RedrawWires(string nodeId)
    {
        foreach (var wire in _wires.Where(wire => wire.Touches(nodeId)))
        {
            wire.Redraw(_surface);
        }
    }

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
