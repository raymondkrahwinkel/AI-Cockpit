using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.Plugins.Abstractions.TimeTracking;
using Cockpit.Plugins.Abstractions.UI;
using Material.Icons;

namespace Cockpit.Plugin.Productivity.UI;

internal sealed class HoursWorkspaceBody : UserControl
{
    private static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");
    private readonly ProductivityController _controller;
    private readonly ICockpitUiHost _host;
    private readonly TextBox _description = new()
    {
        Name = "Description",
        PlaceholderText = "Omschrijving, bv. Telefonisch overleg met klant XXX",
    };
    private readonly TextBox _duration = new() { Name = "Duration", PlaceholderText = "0:15", Width = 105 };
    private readonly ComboBox _project = new() { Name = "Project", Width = 235 };
    private readonly Button _add = new() { Name = "AddEntry", Content = "+ Vastleggen" };
    private readonly Button _start = new() { Name = "StartStopwatch", Content = "▶ Start stopwatch" };
    private readonly TextBlock _reason = Ui.Hint(string.Empty);
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _date = new() { Name = "DayPicker", Width = 115, TextAlignment = TextAlignment.Center };
    private readonly Button _dayLabel = new() { Name = "Today", Classes = { "Subtle", "Compact" } };
    private readonly TextBlock _formDay = Ui.Hint(string.Empty);
    private readonly TextBlock _rulesTitle = new() { FontSize = 15, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _rulesTotal = Ui.Hint(string.Empty);
    private readonly StackPanel _running = new() { Spacing = 8 };
    private readonly StackPanel _rows = new() { Spacing = 0 };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Guid? _editingId;
    private bool _editingStopwatch;
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
        _date.KeyDown += async (_, eventArgs) =>
        {
            if (eventArgs.Key == Key.Enter)
            {
                await _SelectTypedDateAsync();
                eventArgs.Handled = true;
            }
        };
        _date.LostFocus += async (_, _) => await _SelectTypedDateAsync();
        _dayLabel.Click += async (_, _) => await _controller.SelectDateAsync(_Today());

        var previous = new Button { Name = "PreviousDay", Content = "‹", Classes = { "Compact" } };
        previous.Click += async (_, _) => await _controller.SelectDateAsync(_controller.SelectedDate.AddDays(-1));
        var next = new Button { Name = "NextDay", Content = "›", Classes = { "Compact" } };
        next.Click += async (_, _) => await _controller.SelectDateAsync(_controller.SelectedDate.AddDays(1));

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Spacing = 12,
                Margin = new Thickness(24, 16),
                Children =
                {
                    _Header(previous, next),
                    _EntryCard(),
                    _RulesCard(),
                },
            },
        };

        _controller.Changed += _Render;
        _timer.Tick += (_, _) => _RenderRunning();
        _timer.Start();
        DetachedFromVisualTree += (_, _) =>
        {
            _timer.Stop();
            _controller.Changed -= _Render;
        };
        _Render();
    }

    private Control _Header(Button previous, Button next)
    {
        var day = new Button { Content = "Dag", Classes = { "Accent", "Compact" } };
        var week = new Button { Content = "Week", IsEnabled = false, Classes = { "Compact" } };
        ToolTip.SetTip(week, "Komt met AC-1723");
        var segments = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { day, week } };
        var datePicker = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        datePicker.Children.Add(_date);
        var calendar = Ui.IconButton("OpenDayPicker", MaterialIconKind.CalendarMonthOutline, "Kies een datum");
        calendar.IsHitTestVisible = false;
        Grid.SetColumn(calendar, 1);
        datePicker.Children.Add(calendar);

        var meaning = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Ui.Hint("Telt mee als"),
                new TextBlock { Text = "jouw tijd", FontWeight = FontWeight.SemiBold, FontSize = 11.5 },
                Ui.Hint("· gaat mee in het weekvoorstel"),
            },
        };

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,*,Auto"),
            ColumnSpacing = 9,
        };
        header.Children.Add(new TextBlock
        {
            Text = "Uren",
            FontSize = 19,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(segments, 1);
        header.Children.Add(segments);
        Grid.SetColumn(previous, 2);
        header.Children.Add(previous);
        Grid.SetColumn(_dayLabel, 3);
        header.Children.Add(_dayLabel);
        Grid.SetColumn(next, 4);
        header.Children.Add(next);
        Grid.SetColumn(datePicker, 5);
        header.Children.Add(datePicker);
        Grid.SetColumn(meaning, 7);
        header.Children.Add(meaning);
        return Ui.Card(header, 12);
    }

    private Control _EntryCard()
    {
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        heading.Children.Add(new TextBlock { Text = "Snel vastleggen", FontSize = 15, FontWeight = FontWeight.SemiBold });
        Grid.SetColumn(_formDay, 1);
        heading.Children.Add(_formDay);

        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto,Auto"), ColumnSpacing = 8 };
        form.Children.Add(_description);
        Grid.SetColumn(_duration, 1);
        form.Children.Add(_duration);
        var or = Ui.Hint("of");
        or.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(or, 2);
        form.Children.Add(or);
        Grid.SetColumn(_start, 3);
        form.Children.Add(_start);
        Grid.SetColumn(_project, 4);
        form.Children.Add(_project);
        Grid.SetColumn(_add, 5);
        form.Children.Add(_add);

        return Ui.Card(new StackPanel
        {
            Spacing = 9,
            Children =
            {
                heading,
                form,
                Ui.Hint("Duur als 0:15, 15m of 1,5u. Het project is optioneel en staat vooraf op de actieve workspace."),
                _reason,
                _error,
                _running,
            },
        }, 14);
    }

    private Control _RulesCard()
    {
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        heading.Children.Add(_rulesTitle);
        Grid.SetColumn(_rulesTotal, 1);
        heading.Children.Add(_rulesTotal);
        return Ui.Card(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                heading,
                _ColumnHeadings(),
                _rows,
                Ui.Hint("Een regel blijft te bewerken tot hij aangeboden is; daarna staat er ‘aangeboden op …’ of ‘geboekt’ en is hij alleen-lezen."),
            },
        }, 14);
    }

    private static Control _ColumnHeadings()
    {
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("60,*,145,70,90,150,55"),
            ColumnSpacing = 10,
            Margin = new Thickness(6, 0),
        };
        var labels = new[] { "Tijd", "Omschrijving", "Project", "Duur", "Bron", "Status" };
        for (var index = 0; index < labels.Length; index++)
        {
            var label = Ui.Hint(labels[index]);
            Grid.SetColumn(label, index);
            header.Children.Add(label);
        }

        return header;
    }

    private async Task _SaveAsync()
    {
        bool succeeded;
        if (_editingStopwatch)
        {
            succeeded = await _controller.UpdateStopwatchAsync(_description.Text ?? string.Empty, _Project());
        }
        else if (_editingId is { } id)
        {
            succeeded = await _controller.UpdateAsync(
                _controller.Entries.Single(entry => entry.Id == id),
                _description.Text ?? string.Empty,
                _duration.Text ?? string.Empty,
                _Project());
        }
        else
        {
            succeeded = await _controller.AddAsync(
                _description.Text ?? string.Empty,
                _duration.Text ?? string.Empty,
                _Project());
        }

        if (succeeded)
        {
            _editingId = null;
            _editingStopwatch = false;
            _description.Text = string.Empty;
            _duration.Text = string.Empty;
            _SetProject(_controller.ProjectFallback);
            _Render();
        }
    }

    private async Task _StartAsync()
    {
        if (await _controller.StartStopwatchAsync(_description.Text ?? string.Empty, _Project()))
        {
            _description.Text = string.Empty;
            _duration.Text = string.Empty;
        }
    }

    private void _BeginEdit(CockpitTimeEntry entry)
    {
        _editingStopwatch = false;
        _editingId = entry.Id;
        _description.Text = entry.Description;
        _duration.Text = $"{(int)entry.Duration.TotalHours}:{entry.Duration.Minutes:00}";
        _SetProject(entry.ProjectId);
        _Render();
    }

    private void _BeginStopwatchEdit(CockpitRunningStopwatch stopwatch)
    {
        _editingId = null;
        _editingStopwatch = true;
        _description.Text = stopwatch.Description;
        _duration.Text = string.Empty;
        _SetProject(stopwatch.ProjectId);
        _description.Focus();
        _Render();
    }

    private async Task _DeleteAsync(CockpitTimeEntry entry)
    {
        if (await _host.ConfirmAsync("Urenregel verwijderen", $"Verwijder ‘{entry.Description}’?", "Verwijderen"))
        {
            await _controller.DeleteAsync(entry.Id);
        }
    }

    private async Task _SelectTypedDateAsync()
    {
        if (_rendering)
        {
            return;
        }

        if (DateTime.TryParseExact(_date.Text, "dd-MM-yyyy", Dutch, DateTimeStyles.None, out var selected))
        {
            await _controller.SelectDateAsync(DateOnly.FromDateTime(selected));
        }
        else
        {
            _date.Text = _controller.SelectedDate.ToString("dd-MM-yyyy", Dutch);
        }
    }

    private void _Render()
    {
        _rendering = true;
        _date.Text = _controller.SelectedDate.ToString("dd-MM-yyyy", Dutch);
        _rendering = false;
        var date = _controller.SelectedDate.ToDateTime(TimeOnly.MinValue);
        _dayLabel.Content = date.ToString("ddd d MMM yyyy", Dutch) + (_controller.IsToday ? " · vandaag" : string.Empty);
        _formDay.Text = "legt vast op " + (_controller.IsToday ? "vandaag, " : string.Empty) + date.ToString("ddd d MMM", Dutch);
        _rulesTitle.Text = _controller.IsToday
            ? "Mijn regels van vandaag"
            : $"Mijn regels van {date.ToString("dddd d MMMM", Dutch)}";
        _rulesTotal.Text = $"totaal {Ui.Duration(TimeSpan.FromTicks(_controller.Entries.Sum(entry => entry.Duration.Ticks)))}";
        var selectedProject = _project.ItemsSource is null ? _controller.ProjectFallback : _Project();
        _SetProject(selectedProject);

        _add.Content = _editingStopwatch ? "Stopwatch bijwerken" : _editingId is null ? "+ Vastleggen" : "Opslaan";
        _duration.IsEnabled = !_editingStopwatch;
        _start.IsEnabled = _controller.CanStartStopwatch;
        _reason.Text = _controller.StartDisabledReason ?? string.Empty;
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

        var elapsed = DateTimeOffset.Now - stopwatch.StartedAt;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var identity = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
        identity.Children.Add(Ui.Dot("CockpitStatusErrorBrush", 9));
        identity.Children.Add(new TextBlock
        {
            Text = $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}",
            FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace"),
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
        }.Themed(TextBlock.ForegroundProperty, "CockpitStatusBusyBrush"));
        identity.Children.Add(new TextBlock
        {
            Text = stopwatch.Description,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var edit = new Button { Content = "Omschrijving wijzigen", Classes = { "Subtle", "Compact" } };
        edit.Click += (_, _) => _BeginStopwatchEdit(stopwatch);
        actions.Children.Add(edit);
        var stop = new Button { Name = "StopStopwatch", Content = "■ Stop en vastleggen", Classes = { "Subtle", "Compact" } }
            .Themed(Button.ForegroundProperty, "CockpitStatusErrorBrush")
            .Themed(Button.BorderBrushProperty, "CockpitStatusErrorBrush");
        stop.BorderThickness = new Thickness(1);
        stop.Click += async (_, _) => await _controller.StopStopwatchAsync();
        actions.Children.Add(stop);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        row.Children.Add(identity);
        Grid.SetColumn(actions, 1);
        row.Children.Add(actions);
        var details = Ui.Hint($"{stopwatch.ProjectId ?? "geen project"} · gestart {stopwatch.StartedAt.ToLocalTime():HH:mm} · ook zichtbaar in de zijbalk");
        Grid.SetRow(details, 1);
        row.Children.Add(details);
        _running.Children.Add(new Border
        {
            Child = row,
            Padding = new Thickness(12, 9),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
        }.Themed(Border.BorderBrushProperty, "CockpitStatusBusyBrush")
            .Themed(Border.BackgroundProperty, "CockpitStatusBusyChipTintBrush"));
    }

    private void _RenderRows()
    {
        _rows.Children.Clear();
        if (_controller.Entries.Count == 0)
        {
            _rows.Children.Add(new Border { Padding = new Thickness(6, 12), Child = Ui.Hint("Nog geen uren op deze dag.") });
            return;
        }

        foreach (var entry in _controller.Entries)
        {
            _rows.Children.Add(_EntryRow(entry));
        }
    }

    private Control _EntryRow(CockpitTimeEntry entry)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("60,*,145,70,90,150,55"),
            ColumnSpacing = 10,
            Margin = new Thickness(6, 8),
        };
        row.Children.Add(new TextBlock
        {
            Text = entry.StartedAt.ToLocalTime().ToString("HH:mm", Dutch),
            FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace"),
            FontWeight = FontWeight.SemiBold,
        });
        var description = new TextBlock { Text = entry.Description, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(description, 1);
        row.Children.Add(description);

        var project = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (!string.IsNullOrWhiteSpace(entry.ProjectId))
        {
            project.Children.Add(Ui.Dot("CockpitStatusDoneBrush", 7));
        }
        project.Children.Add(Ui.Hint(entry.ProjectId ?? "— geen"));
        Grid.SetColumn(project, 2);
        row.Children.Add(project);

        var duration = new TextBlock { Text = Ui.Duration(entry.Duration) };
        Grid.SetColumn(duration, 3);
        row.Children.Add(duration);
        var source = entry.Source == CockpitTimeEntrySource.Stopwatch
            ? Ui.Pill("stopwatch", "CockpitStatusBusyBrush", "CockpitStatusBusyChipTintBrush")
            : Ui.Pill("duur", "CockpitStatusWaitingBrush", "CockpitStatusWaitingChipTintBrush");
        Grid.SetColumn(source, 4);
        row.Children.Add(source);
        var status = new TextBlock { Text = Ui.Status(entry), FontSize = 11.5 };
        status.Themed(TextBlock.ForegroundProperty, entry.Status == CockpitTimeEntryStatus.Booked
            ? "CockpitStatusDoneBrush"
            : "CockpitTextSecondaryBrush");
        Grid.SetColumn(status, 5);
        row.Children.Add(status);

        if (entry.Status == CockpitTimeEntryStatus.Open)
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            var edit = Ui.IconButton("EditEntry", MaterialIconKind.Pencil, "Bewerken");
            edit.Click += (_, _) => _BeginEdit(entry);
            var delete = Ui.IconButton("DeleteEntry", MaterialIconKind.Close, "Verwijderen");
            delete.Click += async (_, _) => await _DeleteAsync(entry);
            actions.Children.Add(edit);
            actions.Children.Add(delete);
            Grid.SetColumn(actions, 6);
            row.Children.Add(actions);
        }

        return new Border { Child = row, BorderThickness = new Thickness(0, 0, 0, 1) }
            .Themed(Border.BorderBrushProperty, "CockpitHairlineSoftBrush");
    }

    private string? _Project()
    {
        if (_project.SelectedItem is not string selected || selected == "Geen project")
        {
            return null;
        }

        const string prefix = "Project: ";
        const string suffix = " (actieve workspace)";
        return selected.StartsWith(prefix, StringComparison.Ordinal)
            ? selected[prefix.Length..].Replace(suffix, string.Empty, StringComparison.Ordinal)
            : selected;
    }

    private void _SetProject(string? projectId)
    {
        var options = new List<string> { "Geen project" };
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            options.Insert(0, $"Project: {projectId}" + (projectId == _controller.ProjectFallback ? " (actieve workspace)" : string.Empty));
        }

        _project.ItemsSource = options;
        _project.SelectedIndex = 0;
    }

    private static DateOnly _Today() => DateOnly.FromDateTime(DateTimeOffset.Now.LocalDateTime);
}
