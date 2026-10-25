using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions.TimeTracking;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Productivity.UI;

internal sealed class HoursWorkspaceBody : UserControl
{
    private static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");
    private readonly ProductivityController _controller;
    private readonly ICockpitUiHost _host;
    private readonly TextBox _description = new() { Name = "Description", PlaceholderText = "Omschrijving" };
    private readonly TextBox _duration = new() { Name = "Duration", PlaceholderText = "0:15, 15m of 1,5u", Width = 150 };
    private readonly TextBox _project = new() { Name = "Project", PlaceholderText = "Project (optioneel)", Width = 220 };
    private readonly Button _add = new() { Name = "AddEntry", Content = "Toevoegen" };
    private readonly Button _start = new() { Name = "StartStopwatch", Content = "Start stopwatch" };
    private readonly TextBlock _reason = Ui.Hint(string.Empty);
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly DatePicker _date = new() { Name = "DayPicker", Width = 160 };
    private readonly TextBlock _dayTitle = new() { FontSize = 18, FontWeight = FontWeight.SemiBold };
    private readonly StackPanel _running = new() { Spacing = 4 };
    private readonly StackPanel _rows = new() { Spacing = 0 };
    private Guid? _editingId;
    private bool _rendering;

    public HoursWorkspaceBody(ProductivityController controller, ICockpitUiHost host)
    {
        _controller = controller;
        _host = host;
        _error.Themed(TextBlock.ForegroundProperty, "CockpitStatusErrorBrush");
        _add.Classes.Add("Accent");
        _add.Click += async (_, _) => await _SaveAsync();
        _start.Click += async (_, _) => await _StartAsync();
        _duration.KeyDown += async (_, eventArgs) =>
        {
            if (eventArgs.Key == Key.Enter)
            {
                await _SaveAsync();
                eventArgs.Handled = true;
            }
        };
        _date.SelectedDateChanged += async (_, _) =>
        {
            if (!_rendering && _date.SelectedDate is { } selected)
            {
                await _controller.SelectDateAsync(DateOnly.FromDateTime(selected.LocalDateTime));
            }
        };

        var previous = new Button { Name = "PreviousDay", Content = "‹", Classes = { "Compact" } };
        previous.Click += async (_, _) => await _controller.SelectDateAsync(_controller.SelectedDate.AddDays(-1));
        var next = new Button { Name = "NextDay", Content = "›", Classes = { "Compact" } };
        next.Click += async (_, _) => await _controller.SelectDateAsync(_controller.SelectedDate.AddDays(1));
        var today = new Button { Name = "Today", Content = "Vandaag", Classes = { "Compact" } };
        today.Click += async (_, _) => await _controller.SelectDateAsync(DateOnly.FromDateTime(DateTimeOffset.Now.DateTime));

        var navigation = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        navigation.Children.Add(previous);
        Grid.SetColumn(next, 1);
        navigation.Children.Add(next);
        Grid.SetColumn(_date, 2);
        navigation.Children.Add(_date);
        Grid.SetColumn(today, 3);
        navigation.Children.Add(today);
        var day = new Button { Content = "Dag", IsEnabled = false, Classes = { "Compact" } };
        Grid.SetColumn(day, 5);
        navigation.Children.Add(day);
        var week = new Button { Content = "Week · komt", IsEnabled = false, Classes = { "Compact" } };
        Grid.SetColumn(week, 6);
        navigation.Children.Add(week);

        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"), ColumnSpacing = 8 };
        form.Children.Add(_description);
        Grid.SetColumn(_duration, 1);
        form.Children.Add(_duration);
        Grid.SetColumn(_project, 2);
        form.Children.Add(_project);
        Grid.SetColumn(_add, 3);
        form.Children.Add(_add);
        Grid.SetColumn(_start, 4);
        form.Children.Add(_start);

        var page = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(24, 20),
            Children =
            {
                new StackPanel
                {
                    Spacing = 3,
                    Children =
                    {
                        new TextBlock { Text = "Uren", FontSize = 26, FontWeight = FontWeight.SemiBold },
                        Ui.Hint("Leg je eigen tijd vast met een duur of één lopende stopwatch."),
                    },
                },
                Ui.Card(new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock { Text = "Snelle invoer", FontSize = 15, FontWeight = FontWeight.SemiBold },
                        form,
                        _reason,
                        _error,
                        _running,
                    },
                }),
                navigation,
                _dayTitle,
                Ui.Card(_rows, 0),
            },
        };
        Content = new ScrollViewer { Content = page };
        _controller.Changed += _Render;
        DetachedFromVisualTree += (_, _) => _controller.Changed -= _Render;
        _Render();
    }

    private async Task _SaveAsync()
    {
        var succeeded = _editingId is { } id
            ? await _controller.UpdateAsync(
                _controller.Entries.Single(entry => entry.Id == id),
                _description.Text ?? string.Empty,
                _duration.Text ?? string.Empty,
                _project.Text)
            : await _controller.AddAsync(_description.Text ?? string.Empty, _duration.Text ?? string.Empty, _project.Text);
        if (succeeded)
        {
            _editingId = null;
            _description.Text = string.Empty;
            _duration.Text = string.Empty;
            _project.Text = _controller.ProjectFallback;
            _Render();
        }
    }

    private async Task _StartAsync()
    {
        if (await _controller.StartStopwatchAsync(_description.Text ?? string.Empty, _project.Text))
        {
            _description.Text = string.Empty;
            _duration.Text = string.Empty;
        }
    }

    private void _BeginEdit(CockpitTimeEntry entry)
    {
        _editingId = entry.Id;
        _description.Text = entry.Description;
        _duration.Text = $"{(int)entry.Duration.TotalHours}:{entry.Duration.Minutes:00}";
        _project.Text = entry.ProjectId;
        _Render();
    }

    private async Task _DeleteAsync(CockpitTimeEntry entry)
    {
        if (await _host.ConfirmAsync("Urenregel verwijderen", $"Verwijder ‘{entry.Description}’?", "Verwijderen"))
        {
            await _controller.DeleteAsync(entry.Id);
        }
    }

    private void _Render()
    {
        _rendering = true;
        _date.SelectedDate = new DateTimeOffset(_controller.SelectedDate.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(_controller.SelectedDate.ToDateTime(TimeOnly.MinValue)));
        _rendering = false;
        _dayTitle.Text = _controller.SelectedDate.ToDateTime(TimeOnly.MinValue).ToString("dddd d MMMM", Dutch);
        if (string.IsNullOrWhiteSpace(_project.Text) && _editingId is null)
        {
            _project.Text = _controller.ProjectFallback;
        }

        _add.Content = _editingId is null ? "Toevoegen" : "Opslaan";
        _start.IsEnabled = _controller.CanStartStopwatch;
        _reason.Text = _controller.StartDisabledReason ?? (_controller.IsToday ? "De stopwatch gebruikt de omschrijving en het project hierboven." : string.Empty);
        _reason.IsVisible = !string.IsNullOrEmpty(_reason.Text);
        _error.Text = _controller.Error ?? string.Empty;
        _error.IsVisible = _controller.Error is not null;
        _RenderRunning();
        _RenderRows();
    }

    private void _RenderRunning()
    {
        _running.Children.Clear();
        if (_controller.Stopwatch is not { } stopwatch)
        {
            return;
        }

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
        row.Children.Add(Ui.Tag("loopt", "CockpitStatusBusyBrush"));
        var description = new TextBlock { Text = stopwatch.Description, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(description, 1);
        row.Children.Add(description);
        var stop = new Button { Name = "StopStopwatch", Content = "Stop", Classes = { "Compact" } };
        stop.Click += async (_, _) => await _controller.StopStopwatchAsync();
        Grid.SetColumn(stop, 2);
        row.Children.Add(stop);
        _running.Children.Add(Ui.Card(row, 10));
    }

    private void _RenderRows()
    {
        _rows.Children.Clear();
        if (_controller.Entries.Count == 0)
        {
            _rows.Children.Add(new Border { Padding = new Thickness(16), Child = Ui.Hint("Nog geen uren op deze dag.") });
            return;
        }

        foreach (var entry in _controller.Entries)
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("70,*,140,75,90,150,Auto"),
                ColumnSpacing = 10,
                Margin = new Thickness(14, 11),
            };
            row.Children.Add(new TextBlock { Text = entry.StartedAt.ToLocalTime().ToString("HH:mm", Dutch), FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace") });
            var description = new TextBlock { Text = entry.Description, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(description, 1);
            row.Children.Add(description);
            var project = Ui.Hint(entry.ProjectId ?? "—");
            Grid.SetColumn(project, 2);
            row.Children.Add(project);
            var duration = new TextBlock { Text = Ui.Duration(entry.Duration) };
            Grid.SetColumn(duration, 3);
            row.Children.Add(duration);
            var source = Ui.Tag(Ui.Source(entry.Source));
            Grid.SetColumn(source, 4);
            row.Children.Add(source);
            var status = Ui.Tag(Ui.Status(entry), entry.Status == CockpitTimeEntryStatus.Booked ? "CockpitStatusDoneBrush" : "CockpitTextSecondaryBrush");
            Grid.SetColumn(status, 5);
            row.Children.Add(status);
            if (entry.Status == CockpitTimeEntryStatus.Open)
            {
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                var edit = new Button { Name = "EditEntry", Content = "Bewerken", Classes = { "Compact" } };
                edit.Click += (_, _) => _BeginEdit(entry);
                var delete = new Button { Name = "DeleteEntry", Content = "Verwijderen", Classes = { "Compact" } };
                delete.Click += async (_, _) => await _DeleteAsync(entry);
                actions.Children.Add(edit);
                actions.Children.Add(delete);
                Grid.SetColumn(actions, 6);
                row.Children.Add(actions);
            }

            _rows.Children.Add(new Border { Child = row, BorderThickness = new Thickness(0, 0, 0, 1) }
                .Themed(Border.BorderBrushProperty, "CockpitHairlineSoftBrush"));
        }
    }
}
