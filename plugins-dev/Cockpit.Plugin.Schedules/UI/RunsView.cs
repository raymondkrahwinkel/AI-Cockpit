using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.Schedules.Contracts;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Schedules.UI;

// The Run history tab (mockup "Run history"): filter by task and outcome, search the output, and a detail with the
// trigger, the delivery (handed to the assistant included), the output, the delegated task, Run again and Copy.
internal sealed class RunsView : UserControl
{
    private static readonly (string, string)[] Outcomes = [("all", "All"), ("ok", "Succeeded"), ("fail", "Failed"), ("skip", "Nothing delivered")];

    private readonly SchedulesState _state;
    private readonly ICockpitUiHost _host;
    private readonly TextBox _search = new() { PlaceholderText = "Search output", Width = 240 };
    private string _outcome = "all";
    private string? _task;
    private string? _selected;

    public RunsView(SchedulesState state, ICockpitUiHost host)
    {
        _state = state;
        _host = host;
        _state.Changed += Render;
        _search.TextChanged += (_, _) => Render();
        Render();
    }

    public void Select(string? runId)
    {
        _selected = runId;
        Render();
    }

    public void Render()
    {
        var header = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 12),
            Children = { Ui.Title("Run history"), Ui.Hint($"Kept {_state.Settings.KeepDays} days · output up to 256 KB per run") },
        };

        var search = _search.Text ?? string.Empty;
        (_search.Parent as Panel)?.Children.Remove(_search);
        var names = new List<string> { "All tasks" };
        names.AddRange(_state.Tasks.Select(view => view.Task.Name));
        var task = new ComboBox { ItemsSource = names, SelectedIndex = _task is null ? 0 : Math.Max(0, _state.Tasks.FindIndex(view => view.Task.Id == _task) + 1) };
        task.SelectionChanged += (_, _) =>
        {
            _task = task.SelectedIndex <= 0 ? null : _state.Tasks[task.SelectedIndex - 1].Task.Id;
            Render();
        };
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 0, 0, 10),
            Children = { _search, task, Ui.Segments(Outcomes, _outcome, key => { _outcome = key; Render(); }) },
        };

        var runs = _state.Runs.Where(run => run.Outcome != RunOutcome.Missed
            && (_task is null || run.TaskId == _task)
            && _outcome switch
            {
                "ok" => run.Outcome == RunOutcome.Succeeded,
                "fail" => run.Outcome == RunOutcome.Failed,
                "skip" => run.Outcome == RunOutcome.NothingToDeliver,
                _ => true,
            }
            && (search.Length == 0 || (run.Output ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase))).ToList();

        var table = new StackPanel { Children = { _Row(Ui.Muted("Task"), Ui.Muted("When"), Ui.Muted("Outcome"), selected: false) } };
        if (runs.Count == 0)
        {
            table.Children.Add(Ui.Hint("No runs yet."));
        }

        foreach (var run in runs)
        {
            var name = new StackPanel { Children = { new TextBlock { Text = _Name(run.TaskId), FontWeight = FontWeight.SemiBold }, Ui.Hint(Ui.Trigger(run.Trigger)) } };
            var when = new StackPanel { Children = { Ui.Muted(Ui.When(run.StartedAt)), Ui.Hint(Ui.Duration(run.Duration)) } };
            var row = _Row(name, when, Ui.Pill(run), selected: run.Id == _selected);
            row.PointerPressed += (_, _) => { _selected = run.Id; Render(); };
            table.Children.Add(row);
        }

        var selected = runs.FirstOrDefault(run => run.Id == _selected);
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions(selected is null ? "*" : "420,*"), ColumnSpacing = 14 };
        body.Children.Add(new ScrollViewer { Content = table });
        if (selected is not null)
        {
            var detail = _Detail(selected);
            Grid.SetColumn(detail, 1);
            body.Children.Add(detail);
        }

        Content = new DockPanel { Children = { Ui.Docked(header, Dock.Top), Ui.Docked(toolbar, Dock.Top), body } };
    }

    private Control _Detail(RunRecord run)
    {
        var close = Ui.Button("✕", () => { _selected = null; Render(); }, "Compact", "Ghost");
        var head = new DockPanel { Children = { Ui.Docked(close, Dock.Right), new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Ui.Title(_Name(run.TaskId)), Ui.Pill(run) } } } };
        var transcript = run.DelegatedTaskId is { } taskId
            ? (Control)new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    Ui.Value($"Delegated task {taskId}", mono: true),
                    Ui.AsyncButton("Copy id", () => _host.SetClipboardTextAsync(taskId), _Report, "Compact", "Ghost"),
                },
            }
            : Ui.Hint("No agent session for this run.");

        var output = new SelectableTextBlock
        {
            Text = string.IsNullOrEmpty(run.Output) ? "(no output)" : run.Output,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace"),
            FontSize = 12,
        };

        var panel = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                head,
                Ui.KeyValues(
                    ("Started", Ui.Value($"{Ui.When(run.StartedAt)} · {Ui.Duration(run.Duration)}")),
                    ("Trigger", Ui.Value(Ui.Trigger(run.Trigger))),
                    ("Delivery", Ui.Value(run.Reason ?? (run.Outcome == RunOutcome.Succeeded ? "Delivered" : "—"))),
                    ("Session", transcript)),
                Ui.Section("Output"),
                new Border { Padding = new Thickness(10), CornerRadius = new CornerRadius(6), Child = output }.Themed(Border.BackgroundProperty, "CockpitInsetBgBrush"),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        Ui.AsyncButton("Run again", () => _state.RunNowAsync(run.TaskId), _Report, "Compact"),
                        Ui.AsyncButton("Copy", () => _host.SetClipboardTextAsync(run.Output ?? string.Empty), _Report, "Compact"),
                    },
                },
            },
        };

        return Ui.Card(new ScrollViewer { Content = panel });
    }

    private string _Name(string taskId) => _state.Tasks.FirstOrDefault(view => view.Task.Id == taskId)?.Task.Name ?? "(deleted task)";

    private void _Report(Exception exception) => _host.ShowToast($"Schedules: {exception.Message}", PluginToastSeverity.Error);

    private static Border _Row(Control task, Control when, Control outcome, bool selected)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,1.2*,1*"), ColumnSpacing = 10 };
        Control[] cells = [task, when, outcome];
        for (var index = 0; index < cells.Length; index++)
        {
            cells[index].VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(cells[index], index);
            grid.Children.Add(cells[index]);
        }

        var border = new Border { Child = grid, Padding = new Thickness(8, 7), BorderThickness = new Thickness(0, 0, 0, 1) }.Themed(Border.BorderBrushProperty, "CockpitHairlineSoftBrush");
        return selected ? border.Themed(Border.BackgroundProperty, "CockpitAccentSelectionBrush") : border;
    }
}
