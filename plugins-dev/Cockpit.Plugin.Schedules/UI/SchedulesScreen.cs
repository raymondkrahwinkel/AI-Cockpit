using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Cockpit.Plugin.Schedules.Contracts;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Schedules.UI;

// The Schedules screen (AC-1546): no navigation of its own, but tabs inside the plugin, as the mockup draws them, with
// the server switch on the right of the tab bar. Reminders is R3 (AC-1551) and the switch reaches a server with A6.
internal sealed class SchedulesScreen : UserControl
{
    public const string TasksTab = "tasks";
    public const string RemindersTab = "reminders";
    public const string RunsTab = "runs";
    public const string SettingsTab = "settings";

    private readonly SchedulesState _state;
    private readonly ICockpitUiHost _host;
    private readonly IReadOnlyList<string> _profiles;
    private readonly Overlay _overlay = new();
    private readonly ContentControl _page = new();
    private readonly TasksView _tasks;
    private readonly RunsView _runs;
    private readonly SettingsView _settings;
    private readonly StackPanel _tabBar = new() { Orientation = Orientation.Horizontal, Spacing = 2 };

    public SchedulesScreen(SchedulesState state, ICockpitUiHost host, IReadOnlyList<string> profiles, string tab = TasksTab)
    {
        _state = state;
        _host = host;
        _profiles = profiles;
        _state.Changed += () => _RenderTabs();
        _tasks = new TasksView(state, host, _overlay, Edit);
        _runs = new RunsView(state, host);
        _settings = new SettingsView(state, host, profiles);

        var server = new ComboBox { ItemsSource = new[] { "This computer" }, SelectedIndex = 0, IsEnabled = false, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(server, "Managing a connected server's schedules from here comes in a later version.");
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        bar.Children.Add(Ui.Docked(server, Dock.Right));
        var title = Ui.Title("Schedules");
        title.VerticalAlignment = VerticalAlignment.Center;
        title.Margin = new Thickness(0, 0, 16, 0);
        bar.Children.Add(Ui.Docked(title, Dock.Left));
        bar.Children.Add(_tabBar);

        var status = Ui.Hint($"Engine running · cockpit-schedules MCP · {SettingsView.Tools.Length} tools · all sessions");
        status.Margin = new Thickness(0, 0, 0, 10);

        Content = new Panel
        {
            Children =
            {
                new DockPanel { Margin = new Thickness(18), Children = { Ui.Docked(bar, Dock.Top), Ui.Docked(status, Dock.Top), _page } },
                _overlay,
            },
        };
        Show(tab);
    }

    public string Tab { get; private set; } = TasksTab;

    public TasksView Tasks => _tasks;

    public RunsView Runs => _runs;

    public SchedulesState State => _state;

    public TaskEditorView? Editor => _page.Content as TaskEditorView;

    public Overlay Dialog => _overlay;

    public void Show(string tab)
    {
        Tab = tab;
        _RenderTabs();
        _page.Content = tab switch
        {
            RunsTab => _runs,
            SettingsTab => _settings,
            _ => _tasks,
        };
    }

    // The tabs with their counts, as the mockup's "Scheduled tasks 6 · Run history 6".
    private void _RenderTabs()
    {
        _tabBar.Children.Clear();
        var runs = _state.Runs.Count(run => run.Outcome != RunOutcome.Missed);
        foreach (var (key, label) in new[] { (TasksTab, $"Scheduled tasks {_state.Tasks.Count}"), (RemindersTab, "Reminders"), (RunsTab, $"Run history {runs}"), (SettingsTab, "MCP & permissions") })
        {
            var tab = Tab;
            // A tab, as the mockup's: the active one underlined in the accent colour, Reminders greyed until it arrives.
            var button = new Button { Content = label, IsEnabled = key != RemindersTab, Classes = { "Ghost" }, FontWeight = key == tab ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal };
            if (key == RemindersTab)
            {
                button.Opacity = 0.45;
                ToolTip.SetTip(button, "Reminders come in a later version of Schedules.");
            }

            button.Click += (_, _) => Show(key);
            var underline = new Border { Child = button, BorderThickness = new Thickness(0, 0, 0, 2) };
            _tabBar.Children.Add(key == tab ? underline.Themed(Border.BorderBrushProperty, "CockpitAccentBrush") : underline);
        }
    }

    // The editor takes the Scheduled tasks tab's place, as in the mockup, and gives it back when it closes.
    public void Edit(ScheduledTask? task)
    {
        Tab = TasksTab;
        _page.Content = new TaskEditorView(_state, _host, _profiles, task, saved =>
        {
            Show(TasksTab);
            _tasks.Select(saved);
        });
    }
}
