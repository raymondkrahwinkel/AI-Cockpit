using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Cockpit.Plugin.Workflows.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Workflows;

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
    private readonly ICockpitHost _host;
    private readonly IReadOnlyList<WorkflowTemplate> _templates;
    private readonly Action _save;
    private readonly StackPanel _rows;

    public WorkflowManagerControl(List<Workflow> workflows, ICockpitHost host, IReadOnlyList<WorkflowTemplate> templates, Action save)
    {
        _workflows = workflows;
        _host = host;
        _actions = host.Actions;
        _templates = templates;
        _save = save;

        _rows = new StackPanel { Spacing = 6 };

        // Three ways to start, each its own button: a blank canvas, a flow the plugins already know how to draw, and
        // one somebody sent you. They were a menu until the templates outgrew it — a flyout you have to read top to
        // bottom is a list, and a list of thirty is not a menu.
        var newFlow = new Button { Content = "+ New flow", Classes = { "Accent" } };
        newFlow.Click += (_, _) => _Add(new Workflow { Id = Guid.NewGuid().ToString("n"), Name = $"Flow {_workflows.Count + 1}" }, open: true);

        var fromTemplate = new Button { Content = "From template…", Classes = { "Subtle" }, Margin = new Thickness(0, 0, 6, 0) };
        ToolTip.SetTip(fromTemplate, "Start from a flow the plugins already know how to draw");
        fromTemplate.Click += (_, _) => _ = _ShowTemplatesAsync();

        var import = new Button { Content = "Import…", Classes = { "Subtle" }, Margin = new Thickness(0, 0, 6, 0) };
        ToolTip.SetTip(import, "Open a flow somebody exported — it arrives switched off, for you to read before you arm it");
        import.Click += async (_, _) => await _ImportAsync();

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { import, fromTemplate, newFlow },
        };

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);
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

        var export = new Button { Content = "Export", Classes = { "Compact", "Subtle" } };
        ToolTip.SetTip(export, "Write this flow to a file — how you share one, and how a template is made");
        export.Click += async (_, _) => await _ExportAsync(workflow);

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
            Children = { active, open, duplicate, export, delete },
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

    private void _Duplicate(Workflow workflow) => _Add(WorkflowCopy.Of(workflow, $"{workflow.Name} (copy)"), open: false);

    /// <summary>The templates, in a dialog you can search — a menu stops working the moment there are more than a handful.</summary>
    private async Task _ShowTemplatesAsync()
    {
        if (_templates.Count == 0)
        {
            _host.ShowToast("No templates yet — they come from the plugins you install, and from flows you export.", PluginToastSeverity.Information);
            return;
        }

        await _host.ShowDialogAsync("Start from a template", () =>
        {
            var picker = new TemplatePickerControl(_templates);
            picker.Chosen += (_, template) =>
            {
                _FromTemplate(template);
                _CloseDialog(picker);
            };
            picker.ImportRequested += async (_, _) =>
            {
                _CloseDialog(picker);
                await _ImportAsync();
            };

            return picker;
        }, 720, 560);
    }

    // The dialog is the host's, and it owns its window: closing it from the inside means asking the window it is in.
    private static void _CloseDialog(Control content) =>
        (TopLevel.GetTopLevel(content) as Window)?.Close();

    private void _FromTemplate(WorkflowTemplate template)
    {
        if (WorkflowJson.Read(template.Json) is not { } flow)
        {
            _host.ShowToast($"'{template.Name}' could not be read — its plugin wrote a flow this build does not understand.", PluginToastSeverity.Error);
            return;
        }

        _Add(WorkflowCopy.Of(flow, template.Name), open: true);
    }

    /// <summary>Reads a flow somebody sent you. It arrives switched off: a flow you have not read is not one that should already be running.</summary>
    private async Task _ImportAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import a flow",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Cockpit flow") { Patterns = ["*.json"] }],
        });

        if (files.Count == 0)
        {
            return;
        }

        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync();

            if (WorkflowJson.Read(json) is not { } flow)
            {
                _host.ShowToast("That file is not a flow this build can read.", PluginToastSeverity.Error);
                return;
            }

            _Add(WorkflowCopy.Of(flow, flow.Name), open: true);
        }
        catch (Exception exception)
        {
            _host.ShowToast($"Could not import that flow: {exception.Message}", PluginToastSeverity.Error);
        }
    }

    /// <summary>Writes a flow to a file — the way one is shared, and the same text a plugin ships a template as.</summary>
    private async Task _ExportAsync(Workflow workflow)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export this flow",
            SuggestedFileName = $"{workflow.Name}.json",
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("Cockpit flow") { Patterns = ["*.json"] }],
        });

        if (file is null)
        {
            return;
        }

        try
        {
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(WorkflowJson.Write(workflow));

            _host.ShowToast($"'{workflow.Name}' exported.", PluginToastSeverity.Success);
        }
        catch (Exception exception)
        {
            _host.ShowToast($"Could not export that flow: {exception.Message}", PluginToastSeverity.Error);
        }
    }

    private void _Add(Workflow workflow, bool open)
    {
        _workflows.Add(workflow);
        _save();
        Refresh();

        if (open)
        {
            OpenRequested?.Invoke(this, workflow);
        }
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
