using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.Workflows.Canvas;
using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows;

/// <summary>
/// Building one flow (#69): a toolbar that says which flow this is and whether it is armed, the canvas, and the
/// step picker standing open on the right — not hiding behind a click, because "what can I even add" is the
/// question you have while you look at the canvas, not after you have decided to add something.
/// </summary>
internal sealed class WorkflowEditorControl : UserControl
{
    private readonly Workflow _workflow;
    private readonly Action _save;
    private readonly WorkflowCanvas _canvas;
    private readonly NodePicker _picker;
    private readonly TextBlock _status;
    private readonly TextBlock _saved;

    public WorkflowEditorControl(Workflow workflow, Action save)
    {
        _workflow = workflow;
        _save = save;

        _status = new TextBlock { FontSize = 11, Opacity = 0.65, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 6, 12, 6) };
        _saved = new TextBlock { FontSize = 11, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center, Text = "Saved" };

        _picker = new NodePicker();
        _picker.Picked += (_, picked) => _Add(picked);

        _canvas = new WorkflowCanvas(workflow);
        _canvas.Changed += (_, _) => _Touched();
        _canvas.Refused += (_, reason) => _status.Text = reason;
        _canvas.SelectionChanged += (_, _) => _Describe();
        _canvas.AddRequested += (_, from) => _picker.AimAt(from.NodeId, from.Output);
        _canvas.DropRequested += (_, drop) => _Drop(drop.TypeId, drop.X, drop.Y);

        var canvasArea = new Grid();
        canvasArea.Children.Add(_canvas);
        canvasArea.Children.Add(_ViewControls());
        canvasArea.Children.Add(_ExecuteButton());

        var middle = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var toolbar = _Toolbar();
        Grid.SetRow(toolbar, 0);
        Grid.SetRow(canvasArea, 1);
        Grid.SetRow(_status, 2);
        middle.Children.Add(toolbar);
        middle.Children.Add(canvasArea);
        middle.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(_picker, Dock.Right);
        root.Children.Add(_picker);
        root.Children.Add(middle);

        Content = root;
        _Describe();
    }

    /// <summary>Raised when the operator wants the list of flows back.</summary>
    public event EventHandler? BackRequested;

    // Everything about the flow as a whole: which one it is, what it is called, and whether it is armed.
    private Control _Toolbar()
    {
        var back = new Button { Content = "‹  Flows", Classes = { "Compact", "Subtle" } };
        ToolTip.SetTip(back, "Back to the list of flows");
        back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);

        // The name is a text box, not a label with a Rename button behind it: renaming a thing you are looking at
        // should not require asking for permission first.
        var name = new TextBox
        {
            Text = _workflow.Name,
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
            MinWidth = 240,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(name, "The flow's name — type to change it");
        name.LostFocus += (_, _) => _Rename(name.Text);
        name.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                _Rename(name.Text);
                _canvas.Focus();
            }
        };

        var active = new ToggleButton
        {
            Content = _workflow.IsActive ? "Active" : "Inactive",
            IsChecked = _workflow.IsActive,
            Classes = { "Compact" },
        };
        ToolTip.SetTip(active, "An armed flow runs when its trigger fires. Not yet, though: nothing executes a flow until the engine lands.");
        active.IsCheckedChanged += (_, _) =>
        {
            _workflow.IsActive = active.IsChecked == true;
            active.Content = _workflow.IsActive ? "Active" : "Inactive";
            _Touched();
        };

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _saved, active },
        };

        var bar = new DockPanel();
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        bar.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { back, _Separator(), name },
        });

        // An actual bar: it has a floor and a background, so it reads as the flow's own strip rather than as
        // controls floating above the canvas.
        return new Border
        {
            Background = _Brush("CockpitPanelBgBrush"),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 8),
            Child = bar,
        };
    }

    private Control _ViewControls()
    {
        var zoomIn = _IconButton("+", "Zoom in", () => _canvas.ZoomBy(1.2));
        var zoomOut = _IconButton("−", "Zoom out", () => _canvas.ZoomBy(1 / 1.2));
        var reset = _IconButton("⟲", "Reset the view", _canvas.ResetView);

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Children = { zoomIn, zoomOut, reset },
        };
    }

    private static Control _ExecuteButton()
    {
        var execute = new Button
        {
            Content = "▶  Execute workflow",
            Classes = { "Accent" },
            IsEnabled = false,
            Margin = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
        };

        ToolTip.SetTip(execute, "Not yet: the cockpit can draw and save a flow, but nothing runs it. The engine is the next step.");
        ToolTip.SetShowOnDisabled(execute, true);

        return execute;
    }

    private static Control _Separator() => new Border
    {
        Width = 1,
        Margin = new Thickness(2, 4),
        Background = _Brush("CockpitHairlineBrush"),
    };

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;

    private static Button _IconButton(string glyph, string tip, Action onClick)
    {
        var button = new Button { Content = glyph, Classes = { "Compact" }, Width = 28 };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => onClick();

        return button;
    }

    private void _Add(NodePicked picked)
    {
        var (x, y) = _canvas.PlaceAfter(picked.FromNodeId);
        _canvas.Add(
            new WorkflowNode
            {
                Id = Guid.NewGuid().ToString("n"),
                TypeId = picked.Type.Id,
                Name = picked.Type.Name,
                X = x,
                Y = y,
            },
            picked.FromNodeId,
            picked.FromOutput);

        _Describe();
    }

    // A step dragged out of the picker: it lands where it was dropped, wired to nothing — you drew it there
    // because that is where you want it, not because it follows from something.
    private void _Drop(string typeId, double x, double y)
    {
        if (NodeCatalog.Find(typeId) is not { } type)
        {
            return;
        }

        _canvas.Add(new WorkflowNode
        {
            Id = Guid.NewGuid().ToString("n"),
            TypeId = type.Id,
            Name = type.Name,
            X = x,
            Y = y,
        });

        _picker.AimAtNothing();
        _Describe();
    }

    private void _Rename(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed == _workflow.Name)
        {
            return;
        }

        _workflow.Name = trimmed;
        _Touched();
    }

    // The flow is saved as it changes; the toolbar says so rather than offering a Save button that is always
    // already done.
    private void _Touched()
    {
        _workflow.UpdatedAt = DateTimeOffset.UtcNow;
        _save();
        _saved.Text = "Saved";
        _Describe();
    }

    private void _Describe()
    {
        var steps = _workflow.Nodes.Count;
        var wires = _workflow.Connections.Count;

        _status.Text = steps == 0
            ? "Empty. Pick a step on the right to begin — a flow starts with something that triggers it."
            : $"{steps} step(s), {wires} connection(s) — drag a step to move it, pull a wire from a way out, or click a + to add what comes next. Delete removes the selected step. Nothing runs these yet: the engine comes next.";
    }
}
