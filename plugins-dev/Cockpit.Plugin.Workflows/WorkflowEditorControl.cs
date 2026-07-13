using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.Workflows.Canvas;
using Cockpit.Plugin.Workflows.Engine;
using Cockpit.Plugin.Workflows.Model;
using System.Text.Json.Nodes;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Workflows;

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
    private readonly WorkflowEngine _engine;
    private readonly RunStore _runs;
    private readonly RunPanel _runPanel;

    private WorkflowRun? _lastRun;
    private readonly Button _execute;
    private readonly WorkflowCanvas _canvas;
    private readonly NodePicker _picker;
    private readonly NodeDialog _dialog;
    private readonly TextBlock _status;
    private readonly TextBlock _saved;

    public WorkflowEditorControl(Workflow workflow, Action save, ICockpitHost host, RunStore runs, IReadOnlyList<IWorkflowStep> contributed)
    {
        _workflow = workflow;
        _save = save;
        _runs = runs;
        _runPanel = new RunPanel();

        // The steps this build can actually perform. A type without a runner is skipped with a reason at run time,
        // never counted as a success.
        // The same engine the watcher uses: a flow must not do different things depending on who started it.
        _engine = EngineFactory.Create(host, contributed);

        _execute = _ExecuteButton();

        _status = new TextBlock { FontSize = 11, Opacity = 0.65, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 6, 12, 6) };
        _saved = new TextBlock { FontSize = 11, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center, Text = "Saved" };

        _picker = new NodePicker();
        _picker.Picked += (_, picked) => _Add(picked);

        // A step opens over the canvas, not beside it: what a step has to work with is a bigger question than what
        // it is called, and a strip of text boxes on the right cannot show what came in and what went out at once.
        _dialog = new NodeDialog();

        _canvas = new WorkflowCanvas(workflow);
        _canvas.Changed += (_, _) =>
        {
            _Touched();
            _RefreshExecutable();
        };
        _canvas.Refused += (_, reason) => _status.Text = reason;
        _canvas.SelectionChanged += (_, _) => _Describe();
        _canvas.AddRequested += (_, from) =>
        {
            // Asking what comes next is a different question from what this step is set to do: an open step steps
            // aside for the picker, rather than the + quietly doing nothing behind it.
            _CloseSettings();
            _picker.AimAt(from.NodeId, from.Output);
        };
        _canvas.DropRequested += (_, drop) => _Drop(drop.TypeId, drop.X, drop.Y);
        _canvas.OpenRequested += (_, node) => _OpenSettings(node);

        _dialog.Changed += (_, _) =>
        {
            // A renamed step is a step other steps refer to by a different name, and its card now says something
            // else — so the canvas is redrawn as you type, not once you close.
            _Touched();
            _canvas.Rebuild();
        };
        _dialog.CloseRequested += (_, _) => _CloseSettings();

        var canvasArea = new Grid();
        canvasArea.Children.Add(_canvas);
        canvasArea.Children.Add(_ViewControls());
        canvasArea.Children.Add(_execute);

        var middle = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto") };
        var toolbar = _Toolbar();
        Grid.SetRow(toolbar, 0);
        Grid.SetRow(canvasArea, 1);
        Grid.SetRow(_runPanel, 2);
        Grid.SetRow(_status, 3);
        middle.Children.Add(toolbar);
        middle.Children.Add(canvasArea);
        middle.Children.Add(_runPanel);
        middle.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(_picker, Dock.Right);
        root.Children.Add(_picker);
        root.Children.Add(middle);

        Content = new Grid { Children = { root, _dialog } };

        // What a step has to work with comes from the last run, and a run outlives the session it was made in: the
        // history is stored. Without this a flow you ran yesterday opened with "nothing has flowed into this step",
        // which is not true, and is exactly the moment the panes are worth having.
        _lastRun = runs.Load().FirstOrDefault(run => run.WorkflowId == workflow.Id);
        _Describe();
        _RefreshExecutable();
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
        ToolTip.SetTip(active, "An active flow runs by itself when its trigger fires — a session says the thing it watches for, or the clock comes round. Inactive, it only runs when you press Execute.");
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

    // Runs the flow from its manual trigger. Without one there is nothing to press: a flow that starts on an event
    // starts when that event happens, not when you ask it to — so the button says why it is disabled rather than
    // pretending it could.
    private Button _ExecuteButton()
    {
        var execute = new Button
        {
            Content = "▶  Execute workflow",
            Classes = { "Accent" },
            Margin = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
        };

        ToolTip.SetShowOnDisabled(execute, true);
        execute.Click += async (_, _) => await _RunAsync();

        return execute;
    }

    // Which "Run manually" the button starts from. A flow may hold several — one being built next to one that
    // works — and picking the first in the list means pressing Execute on a step that leads nowhere and calling the
    // result green. So: the one you have selected, else one that is actually wired to something, else the first.
    private WorkflowNode? _ManualTrigger()
    {
        var manual = _workflow.Nodes
            .Where(node => node.TypeId == "cockpit.manual" && !node.IsDisabled)
            .ToList();

        if (_canvas.Selected is { TypeId: "cockpit.manual", IsDisabled: false } selected)
        {
            return selected;
        }

        return manual.FirstOrDefault(node => _workflow.Connections.Any(connection => connection.FromNodeId == node.Id))
            ?? manual.FirstOrDefault();
    }

    private void _RefreshExecutable()
    {
        var manual = _ManualTrigger();
        _execute.IsEnabled = manual is not null;
        ToolTip.SetTip(_execute, manual is not null
            ? $"Run this flow now, starting at '{manual.Name}'."
            : "Add a 'Run manually' step to start this flow by hand. A flow that begins on an event starts when the event happens.");
    }

    private async Task _RunAsync()
    {
        if (_ManualTrigger() is not { } trigger)
        {
            return;
        }

        _execute.IsEnabled = false;
        _execute.Content = "Running…";

        try
        {
            var run = await _engine.RunAsync(_workflow, trigger.Id);

            _lastRun = run;
            _runs.Add(run);
            _runPanel.Show(run);
            _canvas.ShowRun(run);
        }
        finally
        {
            _execute.Content = "▶  Execute workflow";
            _RefreshExecutable();
        }
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

    // Double-clicking a step opens what it is configured with. The canvas is where a flow is shaped; this is where
    // a step is told what to actually do.
    private void _OpenSettings(WorkflowNode node)
    {
        // The picker steps aside: it sits outside the dialog, and a list of steps you can see but not reach (the
        // scrim takes the click) is worse than one that is not there.
        _picker.IsVisible = false;
        _dialog.Show(node, _Incoming(node), _Produced(node), _Earlier(node), _Before(node));
    }

    // What flowed into this step in the last run: what the steps wired before it handed on.
    private IReadOnlyList<JsonObject> _Incoming(WorkflowNode node)
    {
        if (_lastRun is not { } run)
        {
            return [];
        }

        var before = _workflow.Connections
            .Where(connection => connection.ToNodeId == node.Id)
            .Select(connection => connection.FromNodeId)
            .ToHashSet(StringComparer.Ordinal);

        return run.Steps
            .Where(step => before.Contains(step.NodeId))
            .SelectMany(step => step.Items)
            .ToList();
    }

    // The steps wired directly before this one. Without a run, their types are what can be said about the data this
    // step will get — as an example, never as fact.
    private IReadOnlyList<WorkflowNode> _Before(WorkflowNode node) =>
        _workflow.Connections
            .Where(connection => connection.ToNodeId == node.Id)
            .Select(connection => _workflow.Node(connection.FromNodeId))
            .OfType<WorkflowNode>()
            .ToList();

    // What this step itself produced last time it ran.
    private IReadOnlyList<JsonObject> _Produced(WorkflowNode node) =>
        _lastRun?.Steps.LastOrDefault(step => step.NodeId == node.Id)?.Items ?? [];

    // Every step that ran before this one, and the fields it handed on — the data this step can reach by name.
    // Taken from what actually flowed, not from what a type claims it might produce.
    private IReadOnlyList<(string Name, IReadOnlyList<string> Fields)> _Earlier(WorkflowNode node)
    {
        if (_lastRun is not { } run)
        {
            return [];
        }

        return run.Steps
            .TakeWhile(step => step.NodeId != node.Id)
            .Select(step => (step.NodeName, step.Fields))
            .ToList();
    }

    private void _CloseSettings()
    {
        _dialog.Hide();
        _picker.IsVisible = true;
        _canvas.Rebuild();
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
            : $"{steps} step(s), {wires} connection(s) — double-click a step (or its ⚙) to say what it should do; drag it to move it; pull a wire from a way out, or click a + to add what comes next. Delete removes the selected step; hover a connection for its ✕.";
    }
}
