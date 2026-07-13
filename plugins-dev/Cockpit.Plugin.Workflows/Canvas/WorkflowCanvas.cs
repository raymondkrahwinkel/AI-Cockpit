using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.Plugin.Workflows.Model;
using Path = Avalonia.Controls.Shapes.Path;

namespace Cockpit.Plugin.Workflows.Canvas;

/// <summary>
/// The flow editor's surface (#69), in n8n's visual language and with the cockpit's own steps: square icon tiles
/// on a dotted grid, wires with arrowheads, a labelled branch on a decision, and a <c>+</c> on every way out that
/// leads nowhere yet — click it and the picker asks what happens next.
/// <para>
/// Plain Avalonia, no node-editor library: none of them run on Avalonia 12 (see spikes/spike-node-editor). It
/// renders a <see cref="Workflow"/> and writes straight back into it, so what you see and what gets saved cannot
/// drift apart. What may be wired to what is the model's business, not the canvas's — the canvas asks, and reports
/// the refusal.
/// </para>
/// </summary>
internal sealed class WorkflowCanvas : Border
{
    private const double MinZoom = 0.3;
    private const double MaxZoom = 2.5;
    private const double GridStep = 16;

    // How far the + sits from the pin it belongs to: far enough to click without hitting the pin, close enough to
    // read as the same thing.
    private const double PlusDistance = 26;

    private readonly Avalonia.Controls.Canvas _surface = new() { Background = Brushes.Transparent, Width = 4000, Height = 3000 };
    private readonly ScaleTransform _zoom = new(1, 1);
    private readonly TranslateTransform _pan = new();

    private readonly Dictionary<string, WorkflowNodeControl> _nodes = new(StringComparer.Ordinal);
    private readonly List<WorkflowWire> _wires = [];
    // Everything the canvas draws around the flow rather than as part of it: the + buttons and the little stubs
    // that lead to them. Tracked so they can all be removed — an earlier version removed the buttons and left the
    // stubs behind, so every mouse move during a drag painted another one and the canvas filled with trails.
    private readonly List<Control> _decorations = [];

    private WorkflowNodeControl? _draggingNode;
    private Point _dragOffset;

    private WorkflowPin? _pendingSource;
    private Path? _pendingWire;
    private bool _pendingDragged;

    private bool _isPanning;
    private Point _panOrigin;

    public WorkflowCanvas(Workflow workflow)
    {
        Workflow = workflow;

        Background = DotGrid.Brush;
        ClipToBounds = true;
        Focusable = true;

        _surface.RenderTransform = new TransformGroup { Children = { _zoom, _pan } };
        _surface.RenderTransformOrigin = RelativePoint.TopLeft;
        Child = _surface;

        // A step dragged out of the picker lands where it is dropped: where you let go is where you meant it.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, _OnDragOver);
        AddHandler(DragDrop.DropEvent, _OnDrop);

        PointerPressed += _OnPointerPressed;
        PointerMoved += _OnPointerMoved;
        PointerReleased += _OnPointerReleased;
        PointerWheelChanged += _OnWheel;
        KeyDown += _OnKeyDown;

        Rebuild();
    }

    public Workflow Workflow { get; }

    /// <summary>Raised when the canvas changed the workflow (a step moved, a wire was drawn, something was deleted) — the cue to save.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when the model refused a wire, carrying the reason, so it can be said out loud instead of the drag silently doing nothing.</summary>
    public event EventHandler<string>? Refused;

    /// <summary>Raised when a "+" on an unconnected way out was clicked — the dialog aims the picker at it.</summary>
    public event EventHandler<(string NodeId, int Output)>? AddRequested;

    /// <summary>Raised when a step was dragged out of the picker and dropped, carrying its type and where it landed.</summary>
    public event EventHandler<(string TypeId, double X, double Y)>? DropRequested;

    /// <summary>Raised when a step was double-clicked — the editor shows what it can be configured with.</summary>
    public event EventHandler<WorkflowNode>? OpenRequested;

    public WorkflowNode? Selected { get; private set; }

    public event EventHandler? SelectionChanged;

    public double Zoom => _zoom.ScaleX;

    /// <summary>Adds a step, optionally wired to the way out the "+" was clicked on — which is what makes the + worth having.</summary>
    public void Add(WorkflowNode node, string? fromNodeId = null, int fromOutput = 0)
    {
        Workflow.Nodes.Add(node);

        if (fromNodeId is not null)
        {
            var rule = Workflow.Connect(fromNodeId, fromOutput, node.Id);
            if (!rule.IsAllowed && rule.Reason is { } reason)
            {
                Refused?.Invoke(this, reason);
            }
        }

        Rebuild();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A free spot for a new step: to the right of the step it follows, or on an empty patch of canvas.</summary>
    public (double X, double Y) PlaceAfter(string? fromNodeId)
    {
        if (fromNodeId is not null && Workflow.Node(fromNodeId) is { } from)
        {
            return (from.X + 220, from.Y);
        }

        var count = Workflow.Nodes.Count;
        return (80 + count % 4 * 220, 80 + count / 4 * 180);
    }

    public void Rebuild()
    {
        _surface.Children.Clear();
        _nodes.Clear();
        _wires.Clear();
        _decorations.Clear();

        foreach (var node in Workflow.Nodes)
        {
            _AddControl(node);
        }

        foreach (var connection in Workflow.Connections)
        {
            _AddWire(connection);
        }

        // The + buttons need the pins laid out to know where to sit, which has not happened yet — so they are
        // placed once the layout pass has run.
        Avalonia.Threading.Dispatcher.UIThread.Post(_RefreshPlusButtons, DispatcherPriority.Loaded);
    }

    /// <summary>Paints the last run onto the flow: where it succeeded, where it was passed by, and where it broke.</summary>
    public void ShowRun(Engine.WorkflowRun? run)
    {
        foreach (var control in _nodes.Values)
        {
            control.ShowRunStatus(null);
        }

        if (run is null)
        {
            return;
        }

        foreach (var step in run.Steps)
        {
            if (!_nodes.TryGetValue(step.NodeId, out var control))
            {
                continue;
            }

            control.ShowRunStatus(step.Status switch
            {
                Engine.RunStatus.Succeeded => "CockpitStatusDoneBrush",
                Engine.RunStatus.Failed => "CockpitStatusWaitingBrush",
                _ => "CockpitTextFaintBrush",
            });
        }
    }

    public void ZoomBy(double factor)
    {
        var next = Math.Clamp(_zoom.ScaleX * factor, MinZoom, MaxZoom);
        _zoom.ScaleX = _zoom.ScaleY = next;
        _RefreshPlusButtons();
    }

    public void ResetView()
    {
        _zoom.ScaleX = _zoom.ScaleY = 1;
        _pan.X = _pan.Y = 0;
        _RefreshPlusButtons();
    }

    private void _AddControl(WorkflowNode node)
    {
        var control = new WorkflowNodeControl(node);
        Avalonia.Controls.Canvas.SetLeft(control, node.X);
        Avalonia.Controls.Canvas.SetTop(control, node.Y);

        control.HeaderPressed += (_, e) => _BeginNodeDrag(control, e);
        control.Opened += (_, _) =>
        {
            _Select(control.Node);
            OpenRequested?.Invoke(this, control.Node);
        };

        control.PinPressed += (_, pin, pointer) =>
        {
            _BeginWire(pin);
            pointer.Capture(this);
        };

        _nodes[node.Id] = control;
        _surface.Children.Add(control);
    }

    private void _AddWire(WorkflowConnection connection)
    {
        if (!_nodes.TryGetValue(connection.FromNodeId, out var from) || !_nodes.TryGetValue(connection.ToNodeId, out var to))
        {
            return;
        }

        var branch = from.Node.Outputs.ElementAtOrDefault(connection.FromOutput);
        var wire = new WorkflowWire(connection, from.OutputPin(connection.FromOutput), to.InputPin(), branch);
        _wires.Add(wire);

        // Behind the nodes: a curve must never cover the thing it connects.
        _surface.Children.Insert(0, wire.Line);
        _surface.Children.Insert(1, wire.Arrow);
        if (wire.Label is not null)
        {
            _surface.Children.Add(wire.Label);
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() => wire.Redraw(_surface), DispatcherPriority.Loaded);
    }

    // A "+" hangs off every way out that leads nowhere: the flow tells you where it is unfinished, and clicking it
    // is how you continue — you never have to know that a step exists before you can reach for it.
    private void _RefreshPlusButtons()
    {
        foreach (var decoration in _decorations)
        {
            _surface.Children.Remove(decoration);
        }

        _decorations.Clear();

        foreach (var (nodeId, control) in _nodes)
        {
            for (var output = 0; output < control.OutputPins.Count; output++)
            {
                if (Workflow.HasConnectionFrom(nodeId, output))
                {
                    continue;
                }

                var pin = control.OutputPin(output);
                var anchor = pin.AnchorOn(_surface);

                var stub = WorkflowWire.NewLine();
                WorkflowWire.Draw(stub, anchor, new Point(anchor.X + PlusDistance, anchor.Y));
                _surface.Children.Insert(0, stub);
                _decorations.Add(stub);

                var plus = _PlusHandle(pin);
                Avalonia.Controls.Canvas.SetLeft(plus, anchor.X + PlusDistance);
                Avalonia.Controls.Canvas.SetTop(plus, anchor.Y - 9);
                _surface.Children.Add(plus);
                _decorations.Add(plus);
            }
        }
    }

    private PlusHandle _PlusHandle(WorkflowPin pin)
    {
        var handle = new PlusHandle(pin);
        handle.Pressed += (_, e) =>
        {
            _BeginWire(pin);
            e.Pointer.Capture(this);
        };

        return handle;
    }

    private void _OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(NodePicker.DragFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void _OnDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetValue(NodePicker.DragFormat) is not { } typeId)
        {
            return;
        }

        // Snapped to the same grid the dots draw, so a dropped step lines up with the ones placed before it.
        var point = e.GetPosition(_surface);
        var x = Math.Round((point.X - WorkflowNodeControl.CardWidth / 2) / GridStep) * GridStep;
        var y = Math.Round((point.Y - WorkflowNodeControl.CardHeight / 2) / GridStep) * GridStep;

        DropRequested?.Invoke(this, (typeId, x, y));
        e.Handled = true;
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
        // Wires are pulled from a way out; dropping on a step is what finishes them.
        if (pin.IsInput)
        {
            return;
        }

        _pendingDragged = false;
        _pendingSource = pin;
        _pendingWire = WorkflowWire.NewLine();
        _surface.Children.Insert(0, _pendingWire);
    }

    // Dropping is on the step, not on its pin: a 10-pixel circle is a target you have to aim at, and connecting
    // two steps should not be a test of the mouse hand.
    private WorkflowNodeControl? _NodeAt(Point onSurface) =>
        _nodes.Values.FirstOrDefault(control =>
        {
            var left = Avalonia.Controls.Canvas.GetLeft(control);
            var top = Avalonia.Controls.Canvas.GetTop(control);

            return onSurface.X >= left
                && onSurface.X <= left + control.Bounds.Width
                && onSurface.Y >= top
                && onSurface.Y <= top + control.Bounds.Height;
        });

    private void _CompleteWire(WorkflowNodeControl target)
    {
        if (_pendingSource is not { } source)
        {
            return;
        }

        var rule = Workflow.Connect(source.Owner.Node.Id, source.OutputIndex, target.Node.Id);
        if (rule.IsAllowed)
        {
            _ClearPendingWire();
            Rebuild();
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (rule.Reason is { } reason)
        {
            Refused?.Invoke(this, reason);
        }

        _ClearPendingWire();
    }

    // The step a wire would land on lights up before you let go: a drop with no feedback is a guess.
    private void _HighlightDropTarget(WorkflowNodeControl? target, WorkflowNodeControl source)
    {
        foreach (var (id, control) in _nodes)
        {
            var isTarget = target is not null
                && !ReferenceEquals(target, source)
                && ReferenceEquals(control, target)
                && (Workflow.Node(id)?.HasInput ?? false);

            control.IsSelected = isTarget || (Selected is not null && id == Selected.Id);
            control.IsDropTarget = isTarget;
            control.HighlightInput(isTarget);
        }
    }

    private void _ClearPendingWire()
    {
        if (_pendingWire is not null)
        {
            _surface.Children.Remove(_pendingWire);
        }

        _pendingWire = null;
        _pendingSource = null;
        _pendingDragged = false;

        // Drop-target rings go with the wire that was being dragged.
        foreach (var (id, control) in _nodes)
        {
            control.IsSelected = Selected is not null && id == Selected.Id;
            control.HighlightInput(false);
        }
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
            // Snapped to the grid the dots draw: a flow built by hand still lines up.
            var x = Math.Round((onSurface.X - _dragOffset.X) / GridStep) * GridStep;
            var y = Math.Round((onSurface.Y - _dragOffset.Y) / GridStep) * GridStep;

            Avalonia.Controls.Canvas.SetLeft(control, x);
            Avalonia.Controls.Canvas.SetTop(control, y);
            control.Node.X = x;
            control.Node.Y = y;

            foreach (var wire in _wires.Where(wire => wire.Touches(control.Node.Id)))
            {
                wire.Redraw(_surface);
            }

            _RefreshPlusButtons();
            return;
        }

        if (_pendingSource is { } source && _pendingWire is { } line)
        {
            _pendingDragged = true;
            WorkflowWire.Draw(line, source.AnchorOn(_surface), onSurface);
            _HighlightDropTarget(_NodeAt(onSurface), source.Owner);
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
        if (_pendingSource is { } source)
        {
            var target = _NodeAt(e.GetPosition(_surface));

            if (_pendingDragged && target is not null && !ReferenceEquals(target, source.Owner))
            {
                _CompleteWire(target);
            }
            else if (!_pendingDragged)
            {
                // Pressed and let go without moving: that is a click on the +, and it asks what comes next.
                _ClearPendingWire();
                AddRequested?.Invoke(this, (source.Owner.Node.Id, source.OutputIndex));
            }
            else
            {
                // Dropped on empty canvas, or back on the step it came from: not a wire.
                _ClearPendingWire();
            }

            e.Pointer.Capture(null);
            _draggingNode = null;
            _isPanning = false;
            return;
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
}
