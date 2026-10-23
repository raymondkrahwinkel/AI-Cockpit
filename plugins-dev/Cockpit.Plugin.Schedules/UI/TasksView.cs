using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.Schedules.Contracts;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Schedules.UI;

// The Scheduled tasks tab (mockup "Scheduled tasks"): the table with on/off, name with profile and the Dangerous tag,
// schedule in words, next run, last run and Run now / Edit; search and the All · Enabled · Disabled · Failed filter;
// and a detail panel with schedule, action, delivery and the recent runs.
internal sealed class TasksView : UserControl
{
    private static readonly (string, string)[] Filters = [("all", "All"), ("on", "Enabled"), ("off", "Disabled"), ("fail", "Failed")];

    private readonly SchedulesState _state;
    private readonly ICockpitUiHost _host;
    private readonly Overlay _overlay;
    private readonly Action<ScheduledTask?> _edit;
    private string _filter = "all";
    private string _search = string.Empty;
    private string? _selected;

    public TasksView(SchedulesState state, ICockpitUiHost host, Overlay overlay, Action<ScheduledTask?> edit)
    {
        _state = state;
        _host = host;
        _overlay = overlay;
        _edit = edit;
        _state.Changed += Render;
        Render();
    }

    public void Select(string? taskId)
    {
        _selected = taskId;
        Render();
    }

    public void Render()
    {
        var enabled = _state.Tasks.Count(view => view.Task.IsEnabled);
        var failed = _state.Tasks.Count(view => _state.LastRun(view.Task.Id)?.Outcome == RunOutcome.Failed);
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(Ui.Docked(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Ui.Button("Import…", () => _host.ShowToast("Importing AI-Hub tasks comes in a later version of Schedules.", PluginToastSeverity.Information), "Subtle"),
                Ui.Button("+ New task", () => _edit(null), "Accent"),
            },
        }, Dock.Right));
        header.Children.Add(new StackPanel { Children = { Ui.Title("Scheduled tasks"), Ui.Hint($"{enabled} enabled · {failed} failed last run") } });

        var search = new TextBox { Text = _search, PlaceholderText = "Search tasks", Width = 260 };
        search.TextChanged += (_, _) =>
        {
            _search = search.Text ?? string.Empty;
            _RenderTable();
        };
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 0, 0, 10),
            Children = { search, Ui.Segments(Filters, _filter, key => { _filter = key; Render(); }) },
        };

        _table = new StackPanel();
        _RenderTable();

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions(_Selected() is null ? "*" : "*,340"), ColumnSpacing = 14 };
        body.Children.Add(new ScrollViewer { Content = _table });
        if (_Selected() is { } selected)
        {
            var side = _Side(selected);
            Grid.SetColumn(side, 1);
            body.Children.Add(side);
        }

        Content = new DockPanel { Children = { Ui.Docked(header, Dock.Top), Ui.Docked(toolbar, Dock.Top), body } };
    }

    private StackPanel _table = new();

    private SchedulesTaskView? _Selected() => _state.Tasks.FirstOrDefault(view => view.Task.Id == _selected);

    private void _RenderTable()
    {
        _table.Children.Clear();
        var rows = _state.Tasks.Where(view => _filter switch
        {
            "on" => view.Task.IsEnabled,
            "off" => !view.Task.IsEnabled,
            "fail" => _state.LastRun(view.Task.Id)?.Outcome == RunOutcome.Failed,
            _ => true,
        } && (_search.Length == 0 || view.Task.Name.Contains(_search, StringComparison.OrdinalIgnoreCase))).ToList();

        _table.Children.Add(_Row(new TextBlock { Text = "On" }, Ui.Muted("Task"), Ui.Muted("Schedule"), Ui.Muted("Next run"), Ui.Muted("Last run"), new Panel(), header: true));
        if (rows.Count == 0)
        {
            _table.Children.Add(Ui.Hint(_state.Tasks.Count == 0 ? "No scheduled tasks yet. Create one with + New task." : "No tasks match."));
            return;
        }

        foreach (var view in rows)
        {
            _table.Children.Add(_TaskRow(view));
        }
    }

    private Control _TaskRow(SchedulesTaskView view)
    {
        var task = view.Task;
        var last = _state.LastRun(task.Id);
        var toggle = new CheckBox { IsChecked = task.IsEnabled, Classes = { "Switch" } };
        toggle.Click += async (_, _) =>
        {
            try
            {
                await ToggleAsync(task, toggle);
            }
            catch (Exception exception)
            {
                _Report(exception);
            }
        };

        var kind = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        kind.Children.Add(Ui.Hint(task.Action.Kind == TaskActionKind.AgentPrompt ? $"Agent · {task.Action.Profile}" : "Fixed message"));
        if (Ui.IsDangerous(task))
        {
            kind.Children.Add(Ui.Tag("Dangerous", "CockpitStatusErrorBrush"));
        }

        var name = new StackPanel { Children = { new TextBlock { Text = task.Name, FontWeight = FontWeight.SemiBold }, kind } };
        var lastRun = new StackPanel { Children = { Ui.Pill(last), Ui.Hint(last is null ? string.Empty : Ui.When(last.StartedAt) + (last.Trigger == RunTrigger.CatchUp ? " · catch-up" : string.Empty)) } };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Children =
            {
                _RunNowButton(task, last, "Compact"),
                Ui.Button("Edit", () => _edit(task), "Compact", "Ghost"),
            },
        };

        var row = _Row(toggle, name, Ui.Muted(view.ReadsAs ?? task.Schedule), new TextBlock { Text = task.IsEnabled ? Ui.When(view.NextRun) : "—" }, lastRun, actions, selected: task.Id == _selected);
        row.PointerPressed += (_, _) => Select(task.Id);
        return row;
    }

    private Button _RunNowButton(ScheduledTask task, RunRecord? last, params string[] classes)
    {
        var button = Ui.AsyncButton("Run now", () => RunNowAsync(task), _Report, classes);
        button.IsEnabled = last?.Outcome != RunOutcome.Running;
        return button;
    }

    private void _Report(Exception exception) => _host.ShowToast($"Schedules: {exception.Message}", PluginToastSeverity.Error);

    private Border _Row(Control on, Control task, Control schedule, Control next, Control last, Control actions, bool header = false, bool selected = false)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("52,2*,1.4*,1*,1.2*,Auto"), ColumnSpacing = 10 };
        Control[] cells = [on, task, schedule, next, last, actions];
        for (var index = 0; index < cells.Length; index++)
        {
            cells[index].VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(cells[index], index);
            grid.Children.Add(cells[index]);
        }

        var border = new Border { Child = grid, Padding = new Thickness(8, header ? 4 : 8), BorderThickness = new Thickness(0, 0, 0, 1) };
        border.Themed(Border.BorderBrushProperty, "CockpitHairlineSoftBrush");
        if (selected)
        {
            border.Themed(Border.BackgroundProperty, "CockpitAccentSelectionBrush");
        }

        return border;
    }

    private Control _Side(SchedulesTaskView view)
    {
        var task = view.Task;
        var runs = _state.Runs.Where(run => run.TaskId == task.Id && run.Outcome != RunOutcome.Missed).Take(6).ToList();
        var panel = new StackPanel { Spacing = 4 };
        var close = Ui.Button("✕", () => Select(null), "Compact", "Ghost");
        var head = new DockPanel { Children = { Ui.Docked(close, Dock.Right), Ui.Title(task.Name) } };
        panel.Children.Add(head);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 8, 0, 0),
            Children =
            {
                _RunNowButton(task, _state.LastRun(task.Id), "Compact", "Accent"),
                Ui.Button("Edit", () => _edit(task), "Compact"),
                Ui.AsyncButton(task.IsEnabled ? "Disable" : "Enable", () => ToggleAsync(task, null), _Report, "Compact"),
                Ui.Button("Delete", () => _ConfirmDelete(task), "Compact", "Ghost"),
            },
        });

        panel.Children.Add(Ui.Section("Schedule"));
        panel.Children.Add(Ui.KeyValues(
            ("When", Ui.Value(view.ReadsAs ?? task.Schedule)),
            ("Cron", Ui.Value(task.Schedule, mono: true)),
            ("Time zone", Ui.Value(task.TimeZone)),
            ("Missed run", Ui.Value(task.MissedRuns == MissedRunPolicy.RunOnceWhenBack ? "Run once when back" : "Skip")),
            ("Next", Ui.Value(task.IsEnabled ? Ui.When(view.NextRun) : "Disabled"))));

        panel.Children.Add(Ui.Section("Action"));
        panel.Children.Add(task.Action.Kind == TaskActionKind.AgentPrompt
            ? Ui.KeyValues(
                ("Type", Ui.Value("Agent prompt")),
                ("Profile", Ui.Value(task.Action.Profile ?? "—")),
                ("Folder", Ui.Value(task.Action.WorkingDirectory ?? "The profile's own", mono: true)),
                ("Permission", Ui.Value(Ui.IsDangerous(task) ? "May write files and run commands (Dangerous)" : "Read-only")),
                ("Unattended", Ui.Value(!Ui.IsDangerous(task) ? "n/a" : task.RunUnattended ? "Yes, runs without asking" : "No, asks consent every run")))
            : Ui.KeyValues(("Type", Ui.Value("Fixed message")), ("Text", Ui.Value(task.Action.Text))));

        panel.Children.Add(Ui.Section("Delivery"));
        var delivery = new WrapPanel();
        foreach (var target in task.Delivery)
        {
            var name = _state.Channels.FirstOrDefault(channel => channel.Id == target.Channel)?.DisplayName ?? $"{target.Channel} (plugin not installed)";
            delivery.Children.Add(Ui.Tag(target.Target is null ? name : $"{name} · {target.Target}"));
        }

        panel.Children.Add(delivery);
        panel.Children.Add(Ui.Hint("Empty result → nothing delivered, run counts as \"Nothing to deliver\"."));

        panel.Children.Add(Ui.Section("Recent runs"));
        if (runs.Count == 0)
        {
            panel.Children.Add(Ui.Hint("No runs yet."));
        }

        foreach (var run in runs)
        {
            panel.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { Ui.Pill(run), Ui.Hint($"{Ui.When(run.StartedAt)} · {Ui.Duration(run.Duration)}") },
            });
        }

        return Ui.Card(new ScrollViewer { Content = panel });
    }

    public async Task RunNowAsync(ScheduledTask task)
    {
        if (await _state.RunNowAsync(task.Id) is null)
        {
            _host.ShowToast($"{task.Name} could not start: it is not stored any more.", PluginToastSeverity.Warning);
            return;
        }

        _host.ShowToast(task.Action.Kind == TaskActionKind.AgentPrompt
            ? $"Running \"{task.Name}\": agent run started with profile {task.Action.Profile}. You can keep working."
            : $"Running \"{task.Name}\": sending the message…");
    }

    // Switching on a Dangerous task that does not run unattended asks first, with the choice the mockup offers.
    public async Task ToggleAsync(ScheduledTask task, CheckBox? toggle)
    {
        if (!task.IsEnabled && Ui.IsDangerous(task) && !task.RunUnattended)
        {
            if (toggle is not null)
            {
                toggle.IsChecked = false;
            }

            _ConfirmEnable(task);
            return;
        }

        await _SetEnabledAsync(task, !task.IsEnabled, task.RunUnattended);
    }

    private void _ConfirmEnable(ScheduledTask task)
    {
        var unattended = new CheckBox
        {
            IsChecked = true,
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "Run unattended" },
                    Ui.Hint("Don't ask me before every scheduled run. Without this, each run waits for consent (desktop card or Discord button, 60 min, then skipped)."),
                },
            },
        };
        var body = new StackPanel
        {
            Children =
            {
                Ui.Note($"This task runs an agent that may write files and run commands in {task.Action.WorkingDirectory ?? "the profile's own folder"}. That makes it a Dangerous step.", warning: true),
                unattended,
            },
        };
        _overlay.Show(
            $"Enable \"{task.Name}\"?",
            body,
            Ui.Button("Cancel", _overlay.Close, "Subtle"),
            Ui.AsyncButton("Enable", () =>
            {
                _overlay.Close();
                return _SetEnabledAsync(task, true, unattended.IsChecked == true);
            }, _Report, "Accent"));
    }

    private void _ConfirmDelete(ScheduledTask task) => _overlay.Show(
        $"Delete \"{task.Name}\"?",
        Ui.Muted("The run history stays readable for 30 days. This can't be undone."),
        Ui.Button("Cancel", _overlay.Close, "Subtle"),
        Ui.AsyncButton("Delete", async () =>
        {
            _overlay.Close();
            await _state.AskAsync<SchedulesResult>(SchedulesChannel.Delete, new SchedulesTaskRequest(task.Id));
            _selected = null;
            await _state.RefreshAsync();
            _host.ShowToast($"Task deleted: {task.Name}");
        }, _Report, "Accent"));

    private async Task _SetEnabledAsync(ScheduledTask task, bool enabled, bool unattended)
    {
        var result = await _state.AskAsync<SchedulesResult>(SchedulesChannel.SetEnabled, new SchedulesEnableRequest(task.Id, enabled, unattended));
        if (result is { Ok: false })
        {
            _host.ShowToast($"{task.Name}: {result.Error}", PluginToastSeverity.Error);
        }

        await _state.RefreshAsync();
        _host.ShowToast(enabled ? $"Task enabled: {task.Name}" : $"Task disabled: {task.Name}");
    }
}
