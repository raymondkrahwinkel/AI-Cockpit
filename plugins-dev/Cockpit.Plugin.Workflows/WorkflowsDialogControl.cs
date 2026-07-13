using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.Workflows.Canvas;
using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows;

/// <summary>
/// The workflow editor (#69): the list of flows on the left, the canvas on the right, and a strip of buttons that
/// drop a node onto it. Every change is saved as it happens — an editor that can lose your work to a closed
/// window is not one you trust with a workflow.
/// <para>
/// This is the canvas half of #69. Nothing runs yet: the nodes are placed and wired, but no engine executes them.
/// The dialog says so rather than implying otherwise, because a flow that looks live but is not is the worst
/// thing this could be.
/// </para>
/// </summary>
internal sealed class WorkflowsDialogControl : UserControl
{
    private readonly WorkflowStore _store;
    private readonly List<Workflow> _workflows;
    private readonly ListBox _list;
    private readonly Border _canvasHost;
    private readonly TextBlock _status;

    private WorkflowCanvas? _canvas;

    public WorkflowsDialogControl(WorkflowStore store)
    {
        _store = store;
        _workflows = [.. store.Load()];

        _status = new TextBlock { FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        _canvasHost = new Border();

        _list = new ListBox
        {
            ItemsSource = _workflows.Select(workflow => workflow.Name).ToList(),
            Width = 190,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _list.SelectionChanged += (_, _) => _OpenSelected();

        var newFlow = new Button { Content = "New flow", Classes = { "Accent" }, HorizontalAlignment = HorizontalAlignment.Stretch };
        newFlow.Click += (_, _) => _NewWorkflow();

        var left = new DockPanel { Width = 190 };
        DockPanel.SetDock(newFlow, Dock.Bottom);
        left.Children.Add(newFlow);
        left.Children.Add(_list);

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 0, 0, 8),
            Children =
            {
                _AddButton("+ Trigger", WorkflowNodeKind.Trigger, "cockpit.event"),
                _AddButton("+ Action", WorkflowNodeKind.Action, "cockpit.notify"),
                _AddButton("+ Decision", WorkflowNodeKind.Decision, "cockpit.if"),
            },
        };

        var right = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        right.Children.Add(toolbar);
        right.Children.Add(_status);
        right.Children.Add(_canvasHost);

        Content = new DockPanel
        {
            Margin = new Thickness(16),
            Children = { left, right },
        };

        if (_workflows.Count == 0)
        {
            _NewWorkflow();
        }
        else
        {
            _list.SelectedIndex = 0;
        }
    }

    private Button _AddButton(string label, WorkflowNodeKind kind, string typeId)
    {
        var button = new Button { Content = label, Classes = { "Compact" } };
        button.Click += (_, _) =>
        {
            if (_canvas is null)
            {
                return;
            }

            // Dropped where there is room rather than always at the origin, so a second node does not land on
            // top of the first.
            var count = _canvas.Workflow.Nodes.Count;
            _canvas.Add(new WorkflowNode
            {
                Id = Guid.NewGuid().ToString("n"),
                TypeId = typeId,
                Kind = kind,
                Title = label[2..],
                X = 60 + count % 3 * 240,
                Y = 60 + count / 3 * 150,
            });
        };

        return button;
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
        canvas.Changed += (_, _) => _Save();
        canvas.Refused += (_, reason) => _status.Text = reason;
        canvas.SelectionChanged += (_, _) => _Describe();

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

        var nodes = canvas.Workflow.Nodes.Count;
        var wires = canvas.Workflow.Connections.Count;
        var selected = canvas.Selected is { } node ? $" · selected: {node.Title}" : string.Empty;

        // Honest about what this is: the flow is drawn and saved, but nothing runs it yet.
        _status.Text = $"{nodes} node(s), {wires} connection(s){selected} — drag a node by its header, pull a wire from an output pin, Delete removes the selected node. Nothing runs these yet: the engine comes next.";
    }

    private void _RefreshList()
    {
        var selected = _list.SelectedIndex;
        _list.ItemsSource = _workflows.Select(workflow => workflow.Name).ToList();
        _list.SelectedIndex = selected;
    }

    private void _Save() => _store.Save(_workflows);
}
