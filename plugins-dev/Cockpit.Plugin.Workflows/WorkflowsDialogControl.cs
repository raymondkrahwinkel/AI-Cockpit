using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.Workflows.Canvas;
using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows;

/// <summary>
/// The workflow editor (#69): the flows on the left, the canvas in the middle, and the step picker sliding in from
/// the right when you ask what happens next. Every change is saved as it happens — an editor that can lose your
/// work to a closed window is not one you trust with a workflow.
/// <para>
/// Nothing runs yet. "Execute workflow" is there, disabled, saying why: the engine is next. A button that quietly
/// did nothing would be worse than no button, and hiding it would hide where this is going.
/// </para>
/// </summary>
internal sealed class WorkflowsDialogControl : UserControl
{
    private readonly WorkflowStore _store;
    private readonly List<Workflow> _workflows;
    private readonly ListBox _list;
    private readonly Border _canvasHost;
    private readonly NodePicker _picker;
    private readonly TextBlock _status;

    private WorkflowCanvas? _canvas;

    public WorkflowsDialogControl(WorkflowStore store)
    {
        _store = store;
        _workflows = [.. store.Load()];

        _status = new TextBlock { FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 8, 2, 0) };
        _canvasHost = new Border { ClipToBounds = true };

        _picker = new NodePicker();
        _picker.Picked += (_, picked) => _AddStep(picked);

        _list = new ListBox { ItemsSource = _workflows.Select(workflow => workflow.Name).ToList() };
        _list.SelectionChanged += (_, _) => _OpenSelected();

        var newFlow = new Button
        {
            Content = "+ New flow",
            Classes = { "Compact" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        };
        newFlow.Click += (_, _) => _NewWorkflow();

        var left = new DockPanel { Width = 190, Margin = new Thickness(0, 0, 12, 0) };
        var flowsHeader = new TextBlock { Text = "FLOWS", FontWeight = FontWeight.SemiBold, FontSize = 10, Opacity = 0.5, Margin = new Thickness(2, 0, 0, 6) };
        DockPanel.SetDock(flowsHeader, Dock.Top);
        DockPanel.SetDock(newFlow, Dock.Bottom);
        left.Children.Add(flowsHeader);
        left.Children.Add(newFlow);
        left.Children.Add(_list);

        // The canvas fills, and the controls float over it — the flow is the thing, not the chrome around it.
        var canvasArea = new Grid();
        canvasArea.Children.Add(_canvasHost);
        canvasArea.Children.Add(_ZoomControls());
        canvasArea.Children.Add(_ExecuteButton());

        var middle = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        Grid.SetRow(canvasArea, 0);
        Grid.SetRow(_status, 1);
        middle.Children.Add(canvasArea);
        middle.Children.Add(_status);

        var withPicker = new DockPanel();
        DockPanel.SetDock(_picker, Dock.Right);
        withPicker.Children.Add(_picker);
        withPicker.Children.Add(middle);

        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(16) };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(withPicker, 1);
        root.Children.Add(left);
        root.Children.Add(withPicker);

        Content = root;

        if (_workflows.Count == 0)
        {
            _NewWorkflow();
        }
        else
        {
            _list.SelectedIndex = 0;
        }
    }

    // Bottom-left, where every canvas tool keeps them.
    private Control _ZoomControls()
    {
        var addStep = new Button { Content = "+ Add step", Classes = { "Compact", "Accent" } };
        addStep.Click += (_, _) => _picker.ShowLoose();

        var zoomIn = _IconButton("+", "Zoom in", () => _canvas?.ZoomBy(1.2));
        var zoomOut = _IconButton("−", "Zoom out", () => _canvas?.ZoomBy(1 / 1.2));
        var reset = _IconButton("⟲", "Reset the view", () => _canvas?.ResetView());

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Children = { addStep, zoomIn, zoomOut, reset },
        };
    }

    private Control _ExecuteButton()
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

        // Disabled, with the reason on it: the editor draws and saves flows, but nothing executes them yet. A
        // button that quietly did nothing would be the worst thing this could ship.
        ToolTip.SetTip(execute, "Not yet: the cockpit can draw and save a flow, but nothing runs it. The engine is the next step.");
        ToolTip.SetShowOnDisabled(execute, true);

        return execute;
    }

    private static Button _IconButton(string glyph, string tip, Action onClick)
    {
        var button = new Button { Content = glyph, Classes = { "Compact" }, Width = 28 };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => onClick();

        return button;
    }

    private void _AddStep(NodePicked picked)
    {
        if (_canvas is not { } canvas)
        {
            return;
        }

        var (x, y) = canvas.PlaceAfter(picked.FromNodeId);
        canvas.Add(
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

    private void _NewWorkflow()
    {
        var workflow = new Workflow
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = $"Flow {_workflows.Count + 1}",
        };

        _workflows.Add(workflow);
        _RefreshList();
        _list.SelectedIndex = _workflows.Count - 1;
        _Save();
    }

    private void _OpenSelected()
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _workflows.Count)
        {
            return;
        }

        var canvas = new WorkflowCanvas(_workflows[_list.SelectedIndex]);
        canvas.Changed += (_, _) =>
        {
            _Save();
            _Describe();
        };
        canvas.Refused += (_, reason) => _status.Text = reason;
        canvas.SelectionChanged += (_, _) => _Describe();
        canvas.AddRequested += (_, from) => _picker.ShowFor(from.NodeId, from.Output);

        _canvas = canvas;
        _canvasHost.Child = canvas;
        _Describe();
    }

    private void _Describe()
    {
        if (_canvas is not { } canvas)
        {
            return;
        }

        var steps = canvas.Workflow.Nodes.Count;
        var wires = canvas.Workflow.Connections.Count;

        _status.Text = steps == 0
            ? "Empty. Add a step to begin — a flow starts with something that triggers it."
            : $"{steps} step(s), {wires} connection(s) — drag a step to move it, pull a wire from a way out, or click a + to add what happens next. Delete removes the selected step. Nothing runs these yet: the engine comes next.";
    }

    private void _RefreshList()
    {
        var selected = _list.SelectedIndex;
        _list.ItemsSource = _workflows.Select(workflow => workflow.Name).ToList();
        _list.SelectedIndex = selected;
    }

    private void _Save() => _store.Save(_workflows);
}
