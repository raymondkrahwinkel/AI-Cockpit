using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.Schedules.Contracts;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Schedules.UI;

// The MCP & permissions tab (mockup "MCP & permissions"): who gets cockpit-schedules (every session, off per profile
// in Profiles → MCP), the restriction on runs a task started, the tools and their kind, and the engine settings.
internal sealed class SettingsView : UserControl
{
    // The tools of cockpit-schedules (A4) and what each does, as the mockup's table.
    public static readonly (string Name, string Kind)[] Tools =
    [
        ("list_scheduled_tasks", "Read"), ("get_scheduled_task", "Read"), ("list_runs", "Read"), ("get_run_output", "Read"),
        ("create_scheduled_task", "Write"), ("update_scheduled_task", "Write"), ("delete_scheduled_task", "Write"), ("set_enabled", "Write"),
        ("run_now", "Run"), ("list_delivery_channels", "Read"), ("list_reminders", "Read"), ("create_reminder", "Write"),
        ("snooze_reminder", "Write"), ("complete_reminder", "Write"), ("delete_reminder", "Write"),
    ];

    private readonly SchedulesState _state;
    private readonly ICockpitUiHost _host;
    private readonly IReadOnlyList<string> _profiles;

    public SettingsView(SchedulesState state, ICockpitUiHost host, IReadOnlyList<string> profiles)
    {
        _state = state;
        _host = host;
        _profiles = profiles;
        _state.Changed += Render;
        Render();
    }

    public void Render()
    {
        var header = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 12),
            Children = { Ui.Title("MCP & permissions"), Ui.Hint("MCP server cockpit-schedules · shipped by this plugin · settings of this computer") },
        };

        var form = new StackPanel { Spacing = 8, MaxWidth = 920, HorizontalAlignment = HorizontalAlignment.Left };
        form.Children.Add(Ui.Card(_WhoGetsTheTools()));
        form.Children.Add(Ui.Card(_ToolTable()));
        form.Children.Add(Ui.Card(_Engine()));
        Content = new DockPanel { Children = { Ui.Docked(header, Dock.Top), new ScrollViewer { Content = form } } };
    }

    private Control _WhoGetsTheTools()
    {
        var profiles = new StackPanel { Spacing = 2 };
        foreach (var profile in _profiles)
        {
            profiles.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { Ui.Tag("On by default", "CockpitStatusDoneBrush"), new TextBlock { Text = profile, VerticalAlignment = VerticalAlignment.Center } },
            });
        }

        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Who gets the tools", FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "Every Cockpit session: the assistant and all agents", FontWeight = FontWeight.SemiBold },
                Ui.Hint("On by default, on this computer and on connected servers. So \"remind me…\", \"what runs tonight?\" and \"schedule this every Monday\" work everywhere: in chat, from Discord, and from any agent."),
                Ui.Muted("Per profile"),
                profiles,
                Ui.Hint("Turn it off for a profile in Profiles → MCP: untick cockpit-schedules there. New profiles get it on."),
                new TextBlock { Text = "Runs started by a task: restricted", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
                Ui.Hint("Only the read tools + create_reminder, so a task can't schedule more tasks. Always on."),
            },
        };
    }

    private static Control _ToolTable()
    {
        var table = new StackPanel { Spacing = 0, Children = { new TextBlock { Text = "Tools", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) } } };
        table.Children.Add(_ToolRow(Ui.Muted("Tool"), Ui.Muted("Kind"), Ui.Muted("When an agent calls it")));
        foreach (var (name, kind) in Tools)
        {
            var when = name switch
            {
                "create_scheduled_task" or "update_scheduled_task" => "Runs; a task with a Dangerous step always arrives disabled.",
                "set_enabled" => "Switching on a task with a Dangerous step always asks you.",
                "run_now" => "run_now on a Dangerous task always asks you.",
                _ when kind == "Read" => "Runs, also for a run a task started.",
                _ when name == "create_reminder" => "Runs, also for a run a task started.",
                _ => "Runs.",
            };
            table.Children.Add(_ToolRow(Ui.Value(name, mono: true), Ui.Muted(kind), Ui.Hint(when)));
        }

        return table;
    }

    private static Border _ToolRow(Control name, Control kind, Control when)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("220,80,*"), ColumnSpacing = 10 };
        Control[] cells = [name, kind, when];
        for (var index = 0; index < cells.Length; index++)
        {
            cells[index].VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(cells[index], index);
            grid.Children.Add(cells[index]);
        }

        return new Border { Child = grid, Padding = new Thickness(4, 5), BorderThickness = new Thickness(0, 0, 0, 1) }.Themed(Border.BorderBrushProperty, "CockpitHairlineSoftBrush");
    }

    private Control _Engine()
    {
        var zone = new TextBox { Text = _state.Settings.DefaultTimeZone, Width = 220 };
        var parallel = new NumericUpDown { Value = _state.Settings.MaxConcurrentRuns, Minimum = 1, Maximum = 16, Increment = 1, Width = 120, FormatString = "0" };
        var save = Ui.AsyncButton("Save", async () =>
        {
            var result = await _state.AskAsync<SchedulesResult>(
                SchedulesChannel.SaveSettings,
                new SchedulesSettingsView((int)(parallel.Value ?? 2), zone.Text?.Trim() ?? string.Empty, _state.Settings.KeepDays));
            _host.ShowToast(result is { Ok: true } ? "Engine settings saved." : $"Not saved: {result?.Error}", result is { Ok: true } ? PluginToastSeverity.Information : PluginToastSeverity.Error);
            await _state.RefreshAsync();
        }, exception => _host.ShowToast($"Schedules: {exception.Message}", PluginToastSeverity.Error), "Accent");

        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Engine", FontWeight = FontWeight.SemiBold },
                Ui.KeyValues(
                    ("Default time zone", zone),
                    ("Max. parallel runs", parallel),
                    ("Keep run history", Ui.Value($"{_state.Settings.KeepDays} days"))),
                save,
            },
        };
    }
}
