using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Cockpit.Plugin.Schedules.Contracts;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Schedules.UI;

// The Schedules screen (AC-1546): no navigation of its own, but tabs inside the plugin, as the mockup draws them, with
// the server switch on the right of the tab bar. AC-1547: the switch binds the same views to a server's host, with a
// banner naming it; a server without this plugin in this version says so instead of showing anything.
internal sealed class SchedulesScreen : UserControl
{
    public const string TasksTab = "tasks";
    public const string RemindersTab = "reminders";
    public const string RunsTab = "runs";
    public const string SettingsTab = "settings";

    private readonly SchedulesSource _local;
    private readonly Views _localViews;
    private readonly IReadOnlyList<PluginServerTarget> _servers;
    private readonly Overlay _overlay = new();
    private readonly ContentControl _page = new();
    private readonly ContentControl _banner = new();
    private readonly StackPanel _tabBar = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly ComboBox _server;
    private readonly TextBlock _status;
    private SchedulesSource _source;
    private Views _views;
    private int _switching;
    private bool _settingSwitch;
    private string? _chosen;

    public SchedulesScreen(
        SchedulesState state,
        ICockpitUiHost host,
        IReadOnlyList<string> profiles,
        string tab = TasksTab,
        IReadOnlyList<PluginServerTarget>? servers = null,
        IReadOnlyList<string>? folders = null)
    {
        _local = new SchedulesSource(null, host, state, profiles, folders ?? []);
        _servers = servers ?? [];
        _source = _local;
        _localViews = _views = _CreateViews(_local);

        var server = _server = new ComboBox
        {
            ItemsSource = new[] { "This computer" }.Concat(_servers.Select(target => target.Name)).ToList(),
            SelectedIndex = 0,
            IsEnabled = _servers.Count > 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(server, _servers.Count > 0 ? "Manage the schedules of this computer or of a connected server." : "No server is connected to this computer.");
        server.SelectionChanged += async (_, _) =>
        {
            if (!_settingSwitch)
            {
                await SwitchAsync(server.SelectedIndex);
            }
        };
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        bar.Children.Add(Ui.Docked(server, Dock.Right));
        var title = Ui.Title("Schedules");
        title.VerticalAlignment = VerticalAlignment.Center;
        title.Margin = new Thickness(0, 0, 16, 0);
        bar.Children.Add(Ui.Docked(title, Dock.Left));
        bar.Children.Add(_tabBar);

        var status = _status = Ui.Hint($"Engine running · cockpit-schedules MCP · {SettingsView.Tools.Length} tools · all sessions");
        status.Margin = new Thickness(0, 0, 0, 10);

        Content = new Panel
        {
            Children =
            {
                new DockPanel { Margin = new Thickness(18), Children = { Ui.Docked(bar, Dock.Top), Ui.Docked(_banner, Dock.Top), Ui.Docked(status, Dock.Top), _page } },
                _overlay,
            },
        };
        DetachedFromVisualTree += (_, _) =>
        {
            // A server still opening when the screen closes is released when it lands, not bound.
            _switching++;
            _Release(_source);
        };
        Show(tab);
    }

    public string Tab { get; private set; } = TasksTab;

    // The server the screen manages, or null for this computer.
    public string? Server => _chosen;

    // Why the chosen server cannot be managed, as the page says it; null while a source is shown.
    public string? Unavailable { get; private set; }

    public TasksView Tasks => _views.Tasks;

    public RunsView Runs => _views.Runs;

    public SchedulesState State => _source.State;

    public TaskEditorView? Editor => _page.Content as TaskEditorView;

    public Overlay Dialog => _overlay;

    public void Show(string tab)
    {
        Tab = tab;
        _RenderTabs();
        _page.Content = tab switch
        {
            RunsTab => _views.Runs,
            SettingsTab => _views.Settings,
            _ => _views.Tasks,
        };
    }

    // AC-1547: 0 is this computer, n the n-th server. A switch that lands after a newer one is dropped.
    public async Task SwitchAsync(int index)
    {
        if (_server.SelectedIndex != index)
        {
            // The switch shows what the screen manages, also when the switch comes from code.
            _settingSwitch = true;
            _server.SelectedIndex = index;
            _settingSwitch = false;
        }

        var attempt = ++_switching;
        Unavailable = null;
        if (index <= 0 || index > _servers.Count)
        {
            _chosen = null;
            _Bind(_local, _localViews);
            return;
        }

        var target = _servers[index - 1];
        _chosen = target.Name;
        SchedulesSource? source = null;
        var failure = target.Unavailable ?? "The server gave no host for Schedules.";
        if (target.Host is { } host)
        {
            try
            {
                source = await SchedulesSource.OpenAsync(host, target.Name);
            }
            catch (Exception exception)
            {
                failure = exception.Message;
            }
        }

        if (attempt != _switching)
        {
            _Release(source);
            return;
        }

        if (source is null)
        {
            _Release(_source);
            _source = _local;
            _views = _localViews;
            Unavailable = failure;
            _banner.Content = null;
            _status.IsVisible = false;
            _RenderTabs();
            _page.Content = new StackPanel
            {
                Spacing = 6,
                Margin = new Thickness(0, 24, 0, 0),
                Children = { Ui.Title($"Schedules not available on {target.Name}"), Ui.Hint(failure) },
            };
            return;
        }

        _Bind(source, _CreateViews(source));
    }

    // The editor takes the Scheduled tasks tab's place, as in the mockup, and gives it back when it closes.
    public void Edit(ScheduledTask? task)
    {
        Tab = TasksTab;
        _page.Content = new TaskEditorView(_source.State, _source.Host, _source.Profiles, task, saved =>
        {
            Show(TasksTab);
            _views.Tasks.Select(saved);
        }, _source.Folders, canBrowse: _source.Server is null);
    }

    private void _Bind(SchedulesSource source, Views views)
    {
        if (!ReferenceEquals(source, _source))
        {
            _Release(_source);
        }

        _source = source;
        _views = views;
        _status.IsVisible = true;
        _banner.Content = source.Server is { } server ? _Banner(server, source.State) : null;
        Show(Tab);
    }

    // The local source is the opener's to dispose; a server's is this screen's.
    private void _Release(SchedulesSource? source)
    {
        if (source is not null && !ReferenceEquals(source, _local))
        {
            source.State.Dispose();
        }
    }

    private Views _CreateViews(SchedulesSource source)
    {
        source.State.Changed += () =>
        {
            if (ReferenceEquals(source, _source))
            {
                _RenderTabs();
                _banner.Content = source.Server is { } server ? _Banner(server, source.State) : null;
            }
        };
        return new Views(new TasksView(source.State, source.Host, _overlay, Edit), new RunsView(source.State, source.Host), new SettingsView(source.State, source.Host, source.Profiles));
    }

    // The mockup's band: which server, what that means, and the server's time on the right.
    private static Border _Banner(string server, SchedulesState state)
    {
        var band = new DockPanel();
        band.Children.Add(Ui.Docked(Ui.Hint($"Server time: {_ServerTime(state)}"), Dock.Right));
        band.Children.Add(new TextBlock
        {
            Text = $"You're managing {server}. Tasks run there, also when this computer is off. Agent runs use the server's profiles.",
            FontSize = 12.5,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        return new Border { Padding = new Thickness(12, 8), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(6), Child = band }
            .Themed(Border.BackgroundProperty, "CockpitAccentSelectionBrush");
    }

    private static string _ServerTime(SchedulesState state)
    {
        if (state.BackendNow is not { } now)
        {
            return "unknown";
        }

        var zone = state.Settings.DefaultTimeZone;
        try
        {
            now = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(zone));
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = "UTC";
        }

        return $"{zone} · {now:HH:mm}";
    }

    // The tabs with their counts, as the mockup's "Scheduled tasks 6 · Run history 6".
    private void _RenderTabs()
    {
        _tabBar.Children.Clear();
        if (Unavailable is not null)
        {
            return;
        }

        var runs = _source.State.Runs.Count(run => run.Outcome != RunOutcome.Missed);
        foreach (var (key, label) in new[] { (TasksTab, $"Scheduled tasks {_source.State.Tasks.Count}"), (RemindersTab, "Reminders"), (RunsTab, $"Run history {runs}"), (SettingsTab, "MCP & permissions") })
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

    private sealed record Views(TasksView Tasks, RunsView Runs, SettingsView Settings);
}
