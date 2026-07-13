using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.Workflows.Model;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Workflows;

/// <summary>
/// Where the flows live (#69): the list you land on, with everything you do <em>to</em> a flow rather than
/// <em>in</em> one — open, rename, duplicate, delete, and arm it or leave it off. The editor is for building a
/// flow; this is for keeping them.
/// <para>
/// The rows say what a flow is: how many steps, whether it is armed, and when you last touched it — the three
/// things you need to pick one out of ten.
/// </para>
/// </summary>
internal sealed class WorkflowManagerControl : UserControl
{
    private readonly List<Workflow> _workflows;
    private readonly ICockpitActions _actions;
    private readonly Action _save;
    private readonly StackPanel _rows;

    public WorkflowManagerControl(List<Workflow> workflows, ICockpitActions actions, Action save)
    {
        _workflows = workflows;
        _actions = actions;
        _save = save;

        _rows = new StackPanel { Spacing = 6 };

        var newFlow = new Button { Content = "+ New flow", Classes = { "Accent" } };
        newFlow.Click += (_, _) => _New();

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(newFlow, Dock.Right);
        header.Children.Add(newFlow);
        header.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "Flows", FontSize = 16, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = "A flow is armed or it is not. Nothing runs yet — the engine is the next step.",
                    FontSize = 11,
                    Opacity = 0.6,
                },
            },
        });

        Content = new DockPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                _Docked(header, Dock.Top),
                new ScrollViewer { Content = _rows },
            },
        };

        Refresh();
    }

    /// <summary>Raised with the flow to open in the editor.</summary>
    public event EventHandler<Workflow>? OpenRequested;

    public void Refresh()
    {
        _rows.Children.Clear();

        if (_workflows.Count == 0)
        {
            _rows.Children.Add(new TextBlock
            {
                Text = "No flows yet. A flow is a trigger and what follows from it.",
                Opacity = 0.6,
                FontSize = 12,
                Margin = new Thickness(2, 8, 0, 0),
            });
            return;
        }

        foreach (var workflow in _workflows.OrderByDescending(flow => flow.UpdatedAt))
        {
            _rows.Children.Add(_Row(workflow));
        }
    }

    private Border _Row(Workflow workflow)
    {
        var open = new Button { Content = "Open", Classes = { "Compact" } };
        open.Click += (_, _) => OpenRequested?.Invoke(this, workflow);

        var duplicate = new Button { Content = "Duplicate", Classes = { "Compact", "Subtle" } };
        duplicate.Click += (_, _) => _Duplicate(workflow);

        var delete = new Button { Content = "Delete", Classes = { "Compact", "Subtle" } };
        delete.Click += async (_, _) => await _DeleteAsync(workflow);

        // Armed or not, said in one word — and it is a toggle, not a label, because it is the one property of a
        // flow you change without opening it.
        var active = new ToggleButton
        {
            Content = workflow.IsActive ? "Active" : "Inactive",
            IsChecked = workflow.IsActive,
            Classes = { "Compact" },
        };
        ToolTip.SetTip(active, "An armed flow runs when its trigger fires. Not yet, though: nothing executes a flow until the engine lands.");
        active.IsCheckedChanged += (_, _) =>
        {
            workflow.IsActive = active.IsChecked == true;
            active.Content = workflow.IsActive ? "Active" : "Inactive";
            _Touch(workflow);
        };

        var steps = workflow.Nodes.Count;
        var meta = new TextBlock
        {
            Text = $"{steps} step{(steps == 1 ? string.Empty : "s")}  ·  edited {_Ago(workflow.UpdatedAt)}",
            FontSize = 11,
            Opacity = 0.55,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { active, open, duplicate, delete },
        };

        var row = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = workflow.Name, FontSize = 13, FontWeight = FontWeight.SemiBold },
                meta,
            },
        });

        return new Border
        {
            Background = _Brush("CockpitSecondaryBgBrush"),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10),
            Child = row,
        };
    }

    private void _New()
    {
        var workflow = new Workflow
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = $"Flow {_workflows.Count + 1}",
        };

        _workflows.Add(workflow);
        _save();
        Refresh();
        OpenRequested?.Invoke(this, workflow);
    }

    private void _Duplicate(Workflow workflow)
    {
        var copy = new Workflow
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = $"{workflow.Name} (copy)",
            IsActive = false,
        };

        // New ids throughout: two flows sharing a step id would be one flow with two names.
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in workflow.Nodes)
        {
            var id = Guid.NewGuid().ToString("n");
            idMap[node.Id] = id;
            copy.Nodes.Add(new WorkflowNode
            {
                Id = id,
                TypeId = node.TypeId,
                Name = node.Name,
                X = node.X,
                Y = node.Y,
                IsDisabled = node.IsDisabled,
                Parameters = new Dictionary<string, string>(node.Parameters),
            });
        }

        foreach (var connection in workflow.Connections)
        {
            copy.Connections.Add(new WorkflowConnection
            {
                FromNodeId = idMap[connection.FromNodeId],
                FromOutput = connection.FromOutput,
                ToNodeId = idMap[connection.ToNodeId],
            });
        }

        _workflows.Add(copy);
        _save();
        Refresh();
    }

    private async Task _DeleteAsync(Workflow workflow)
    {
        // Deleting a flow is not undoable — the cockpit's own confirmation, not a bespoke one.
        if (!await _actions.ConfirmAsync("Delete flow", $"Delete '{workflow.Name}'? Its steps and wiring go with it.", "Delete"))
        {
            return;
        }

        _workflows.Remove(workflow);
        _save();
        Refresh();
    }

    private void _Touch(Workflow workflow)
    {
        workflow.UpdatedAt = DateTimeOffset.UtcNow;
        _save();
    }

    private static string _Ago(DateTimeOffset when)
    {
        var elapsed = DateTimeOffset.UtcNow - when;

        return elapsed switch
        {
            { TotalMinutes: < 1 } => "just now",
            { TotalHours: < 1 } => $"{(int)elapsed.TotalMinutes} min ago",
            { TotalDays: < 1 } => $"{(int)elapsed.TotalHours} h ago",
            _ => $"{(int)elapsed.TotalDays} d ago",
        };
    }

    private static Control _Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
