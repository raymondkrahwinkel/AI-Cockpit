using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Cockpit.Plugin.Schedules.Contracts;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Schedules.UI;

// The editor (mockup "New scheduled task"): name; When, as a plain builder or a cron, with the zone, the missed-run
// policy and the next three runs; What, an agent prompt or a fixed message; and where the result goes, the channels
// of the installed communication plugins with a target each. The builder writes cron and reads it back in words.
internal sealed class TaskEditorView : UserControl
{
    public const string InvalidSchedule = "Not a valid cron expression";

    private static readonly (string Key, string Label)[] Days =
        [("1", "Mon"), ("2", "Tue"), ("3", "Wed"), ("4", "Thu"), ("5", "Fri"), ("6", "Sat"), ("0", "Sun")];

    private readonly SchedulesState _state;
    private readonly ICockpitUiHost _host;
    private readonly ScheduledTask? _existing;
    private readonly Action<string?> _close;
    private readonly HashSet<string> _deliver = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _targets = new(StringComparer.Ordinal);
    private readonly TextBlock _previewText = new() { FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _previewNext = Ui.Hint(string.Empty);
    private readonly Button _save;
    private readonly IReadOnlyList<string> _profiles;

    // AC-1547: the folders of the projects where the task lives; a server's task cannot take a folder picked here.
    private readonly IReadOnlyList<string> _folders;
    private readonly bool _canBrowse;
    private int _previewVersion;

    private string _name;
    private bool _cron;
    private string _frequency = "weekly";
    private string _time = "07:30";
    private string _date = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private HashSet<string> _days = ["1", "2", "3", "4", "5"];
    private string _every = "30";
    private string _cronText = "30 7 * * 1-5";
    private string _zone;
    private MissedRunPolicy _missed;
    private TaskActionKind _kind;
    private string? _profile;
    private string _folder;
    private string _text;
    private bool _write;

    public TaskEditorView(
        SchedulesState state,
        ICockpitUiHost host,
        IReadOnlyList<string> profiles,
        ScheduledTask? existing,
        Action<string?> close,
        IReadOnlyList<string>? folders = null,
        bool canBrowse = true)
    {
        _state = state;
        _profiles = profiles;
        _folders = folders ?? [];
        _canBrowse = canBrowse;
        _host = host;
        _existing = existing;
        _close = close;
        _name = existing?.Name ?? string.Empty;
        _zone = existing?.TimeZone ?? state.Settings.DefaultTimeZone;
        _missed = existing?.MissedRuns ?? MissedRunPolicy.RunOnceWhenBack;
        _kind = existing?.Action.Kind ?? TaskActionKind.AgentPrompt;
        _profile = existing?.Action.Profile ?? profiles.FirstOrDefault();
        _folder = existing?.Action.WorkingDirectory ?? string.Empty;
        _text = existing?.Action.Text ?? string.Empty;
        _write = existing?.Action.Permission == AgentPermission.Write;
        foreach (var target in existing?.Delivery ?? [new DeliveryTarget(DeliveryTarget.CockpitNotification)])
        {
            _deliver.Add(target.Channel);
            if (target.Target is { } chosen)
            {
                _targets[target.Channel] = chosen;
            }
        }

        if (existing is not null)
        {
            _ReadSchedule(existing.Schedule);
        }

        // Off until the first preview says the schedule reads.
        _save = Ui.AsyncButton(existing is null ? "Create task" : "Save", SaveAsync, _Report, "Accent");
        _save.IsEnabled = false;
        _Render();
    }

    // The schedule the builder stands for right now, in the engine's own forms.
    public string Schedule
    {
        get
        {
            if (_cron)
            {
                return _cronText.Trim();
            }

            var (hour, minute) = _Time();
            return _frequency switch
            {
                "once" => $"once {_date} {hour:00}:{minute:00}",
                "interval" => $"every {_every.Trim()} m",
                "daily" => $"{minute} {hour} * * *",
                "monthly" => $"{minute} {hour} 1 * *",
                _ => $"{minute} {hour} * * {_DayField()}",
            };
        }
    }

    public bool IsScheduleValid { get; private set; }

    public string PreviewText => _previewText.Text ?? string.Empty;

    public bool CanSave => _save.IsEnabled;

    public void SetCron(string cron)
    {
        _cron = true;
        _cronText = cron;
        _Render();
    }

    // Back to the builder only when it can say the cron; one it cannot, such as */5, stays a cron rather than being lost.
    public void SetPlain()
    {
        if (!_cron || _ReadSchedule(_cronText))
        {
            _cron = false;
        }

        _Render();
    }

    public void SetWeekly(string time, params string[] days)
    {
        _cron = false;
        _frequency = "weekly";
        _time = time;
        _days = [.. days];
        _Render();
    }

    public IReadOnlyList<string> DeliveryChoices => [.. _state.Channels.Select(channel => channel.DisplayName)];

    public Task PreviewSettledAsync() => _previewTask;

    private Task _previewTask = Task.CompletedTask;

    public async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(_name))
        {
            _host.ShowToast("Name is required: give the task a name.", PluginToastSeverity.Warning);
            return;
        }

        // The task as it stands now, not as it stood when the editor opened: a switch flipped meanwhile is kept.
        var current = _state.Tasks.FirstOrDefault(view => view.Task.Id == _existing?.Id)?.Task ?? _existing;
        var dangerous = _kind == TaskActionKind.AgentPrompt && _write;
        var becameDangerous = dangerous && (current is null || !Ui.IsDangerous(current));
        var task = new ScheduledTask
        {
            Id = _existing?.Id ?? string.Empty,
            Name = _name.Trim(),
            Schedule = Schedule,
            TimeZone = _zone.Trim(),
            MissedRuns = _missed,
            IsEnabled = !becameDangerous && (current?.IsEnabled ?? true),
            RunUnattended = !becameDangerous && (current?.RunUnattended ?? false),
            Action = new TaskAction(_kind, _text.Trim())
            {
                Profile = _kind == TaskActionKind.AgentPrompt ? _profile : null,
                WorkingDirectory = _kind == TaskActionKind.AgentPrompt && _folder.Trim().Length > 0 ? _folder.Trim() : null,
                Permission = dangerous ? AgentPermission.Write : AgentPermission.ReadOnly,
                Timeout = _existing?.Action.Timeout,
            },
            Delivery = [.. _deliver.Select(channel => new DeliveryTarget(channel, _targets.GetValueOrDefault(channel)))],
        };

        var result = await _state.AskAsync<SchedulesResult>(SchedulesChannel.Save, task);
        if (result is not { Ok: true })
        {
            _host.ShowToast($"Not saved: {result?.Error ?? "the backend did not answer"}", PluginToastSeverity.Error);
            return;
        }

        await _state.RefreshAsync();
        _host.ShowToast(becameDangerous ? $"{task.Name} is saved and stays disabled until you enable it (Dangerous step)." : $"Task saved: {task.Name}");
        _close(result.Id);
    }

    private void _Render()
    {
        var back = Ui.Button("←", () => _close(_existing?.Id), "Compact", "Ghost");
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 0, 0, 12),
            Children = { back, new StackPanel { Children = { Ui.Title(_existing is null ? "New scheduled task" : "Edit task"), Ui.Hint("Runs on this computer") } } },
        };

        var form = new StackPanel { Spacing = 4, MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
        var name = new TextBox { Text = _name, PlaceholderText = "e.g. Weekly backup check", Width = 420 };
        name.TextChanged += (_, _) => _name = name.Text ?? string.Empty;
        form.Children.Add(Ui.Card(_Field("Name", name)));
        form.Children.Add(Ui.Card(_When()));
        form.Children.Add(Ui.Card(_What()));
        form.Children.Add(Ui.Card(_Deliver()));

        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        footer.Children.Add(Ui.Button("Cancel", () => _close(_existing?.Id), "Subtle"));
        if (_kind == TaskActionKind.AgentPrompt)
        {
            footer.Children.Add(Ui.AsyncButton("Test run", _TestRunAsync, _Report, "Subtle"));
        }

        (_save.Parent as Panel)?.Children.Remove(_save);
        footer.Children.Add(_save);
        form.Children.Add(footer);

        Content = new DockPanel { Children = { Ui.Docked(header, Dock.Top), new ScrollViewer { Content = form } } };
        _previewTask = _PreviewAsync();
    }

    private Control _When()
    {
        var panel = new StackPanel { Spacing = 8, Children = { _Heading("1", "When") } };
        panel.Children.Add(_Field("Schedule", Ui.Segments([("plain", "Plain"), ("cron", "Cron")], _cron ? "cron" : "plain", key =>
        {
            if (key == "cron")
            {
                _cronText = Schedule;
                SetCron(_cronText);
            }
            else
            {
                SetPlain();
            }
        })));

        if (_cron)
        {
            var cron = new TextBox { Text = _cronText, Width = 240, FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace") };
            cron.TextChanged += (_, _) =>
            {
                _cronText = cron.Text ?? string.Empty;
                _previewTask = _PreviewAsync();
            };
            panel.Children.Add(_Field("Cron", new StackPanel { Children = { cron, Ui.Hint("5 fields: minute hour day-of-month month day-of-week. Same parser the MCP tool uses.") } }));
        }
        else
        {
            var frequency = new ComboBox { ItemsSource = new[] { "Once", "Every N minutes", "Every day", "On certain days", "Every month" }, SelectedIndex = Array.IndexOf(new[] { "once", "interval", "daily", "weekly", "monthly" }, _frequency) };
            frequency.SelectionChanged += (_, _) =>
            {
                _frequency = new[] { "once", "interval", "daily", "weekly", "monthly" }[Math.Max(0, frequency.SelectedIndex)];
                _Render();
            };
            var repeat = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { frequency } };
            if (_frequency == "interval")
            {
                var every = new TextBox { Text = _every, Width = 70 };
                every.TextChanged += (_, _) => { _every = every.Text ?? string.Empty; _previewTask = _PreviewAsync(); };
                repeat.Children.Add(every);
                repeat.Children.Add(Ui.Muted("minutes"));
            }
            else
            {
                var time = new TextBox { Text = _time, Width = 70 };
                time.TextChanged += (_, _) => { _time = time.Text ?? string.Empty; _previewTask = _PreviewAsync(); };
                repeat.Children.Add(Ui.Muted("at"));
                repeat.Children.Add(time);
                if (_frequency == "once")
                {
                    var date = new TextBox { Text = _date, Width = 110 };
                    date.TextChanged += (_, _) => { _date = date.Text ?? string.Empty; _previewTask = _PreviewAsync(); };
                    repeat.Children.Add(Ui.Muted("on"));
                    repeat.Children.Add(date);
                }
            }

            foreach (var part in repeat.Children)
            {
                part.VerticalAlignment = VerticalAlignment.Center;
            }

            panel.Children.Add(_Field("Repeat", repeat));
            if (_frequency == "weekly")
            {
                var days = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
                foreach (var (key, label) in Days)
                {
                    var day = new Avalonia.Controls.Primitives.ToggleButton { Content = label, IsChecked = _days.Contains(key), Classes = { "Compact" } };
                    day.Click += (_, _) =>
                    {
                        if (!_days.Remove(key))
                        {
                            _days.Add(key);
                        }

                        _previewTask = _PreviewAsync();
                    };
                    days.Children.Add(day);
                }

                panel.Children.Add(_Field("Days", days));
            }
        }

        var zone = new ComboBox { ItemsSource = new[] { _zone, _state.Settings.DefaultTimeZone, "UTC" }.Distinct().ToList(), SelectedItem = _zone };
        zone.SelectionChanged += (_, _) => { _zone = zone.SelectedItem as string ?? _zone; _previewTask = _PreviewAsync(); };
        panel.Children.Add(_Field("Time zone", zone));

        var missed = new StackPanel { Spacing = 4 };
        missed.Children.Add(_Option(_missed == MissedRunPolicy.RunOnceWhenBack, "Run once when back", "After sleep, shutdown or a restart: one catch-up run, never several.", () => { _missed = MissedRunPolicy.RunOnceWhenBack; _Render(); }));
        missed.Children.Add(_Option(_missed == MissedRunPolicy.Skip, "Skip it", "For tasks that only make sense on time. Logged as \"missed\".", () => { _missed = MissedRunPolicy.Skip; _Render(); }));
        panel.Children.Add(_Field("If a run was missed", missed));

        // The two preview lines outlive a redraw, so they leave the old tree before they join the new one.
        (_previewText.Parent as Panel)?.Children.Remove(_previewText);
        (_previewNext.Parent as Panel)?.Children.Remove(_previewNext);
        var preview = Ui.Note(string.Empty, warning: false);
        preview.Child = new StackPanel { Children = { _previewText, _previewNext } };
        panel.Children.Add(preview);
        return panel;
    }

    private Control _What()
    {
        var panel = new StackPanel { Spacing = 8, Children = { _Heading("2", "What") } };
        panel.Children.Add(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                _Option(_kind == TaskActionKind.AgentPrompt, "Agent prompt", "Starts an agent run with a profile and folder; its answer is the result.", () => { _kind = TaskActionKind.AgentPrompt; _Render(); }),
                _Option(_kind == TaskActionKind.Notification, "Fixed message", "No agent. Delivers the text as-is — cheap and instant.", () => { _kind = TaskActionKind.Notification; _Render(); }),
            },
        });

        if (_kind == TaskActionKind.Notification)
        {
            panel.Children.Add(_Field("Message", _Text(_text, "e.g. Time for the monthly investor review.", 3, value => _text = value)));
            return panel;
        }

        var profile = new ComboBox { ItemsSource = _profiles, SelectedItem = _profile, PlaceholderText = "Choose a profile", MinWidth = 220 };
        profile.SelectionChanged += (_, _) => _profile = profile.SelectedItem as string;
        panel.Children.Add(_Field("Profile", profile));
        var folder = new TextBox { Text = _folder, Width = 380, FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace") };
        folder.TextChanged += (_, _) => _folder = folder.Text ?? string.Empty;
        var browse = Ui.AsyncButton("Browse", async () =>
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is { CanPickFolder: true } storage
                && (await storage.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = "Working folder" })).FirstOrDefault()?.TryGetLocalPath() is { } picked)
            {
                folder.Text = picked;
            }
        }, _Report, "Compact");
        var folderRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { folder } };
        if (_canBrowse)
        {
            folderRow.Children.Add(browse);
        }

        if (_folders.Count > 0)
        {
            var project = new ComboBox { ItemsSource = _folders, PlaceholderText = "From a project", MinWidth = 160 };
            project.SelectionChanged += (_, _) => folder.Text = project.SelectedItem as string ?? folder.Text;
            folderRow.Children.Add(project);
        }
        panel.Children.Add(_Field("Working folder", new StackPanel { Children = { folderRow, Ui.Hint("Must be an allowed folder of the profile. $AI_OS_ROOT and other env vars are expanded.") } }));
        panel.Children.Add(_Field("Prompt", _Text(_text, "What should the agent do? Its final answer is delivered.", 5, value => _text = value)));

        var write = new CheckBox
        {
            IsChecked = _write,
            Content = new StackPanel { Children = { new TextBlock { Text = "Allow writing files and running commands" }, Ui.Hint("Off = read-only run.") } },
        };
        write.Click += (_, _) => { _write = write.IsChecked == true; _Render(); };
        var permission = new StackPanel { Children = { write } };
        if (_write)
        {
            permission.Children.Add(Ui.Note("Dangerous step. When you enable this task, Cockpit asks whether it may run unattended. Tasks created by an agent over MCP always arrive disabled.", warning: true));
        }

        panel.Children.Add(_Field("Permission", permission));
        return panel;
    }

    private Control _Deliver()
    {
        var panel = new StackPanel { Spacing = 6, Children = { _Heading("3", "Deliver the result") } };
        foreach (var channel in _state.Channels)
        {
            var on = _deliver.Contains(channel.Id);
            var subtitle = channel.Id == DeliveryTarget.CockpitNotification
                ? "Toast in the cockpit, also with this plugin closed."
                : $"Sends via the {channel.DisplayName} plugin. Target below.";
            var check = new CheckBox { IsChecked = on, Content = new StackPanel { Children = { new TextBlock { Text = channel.DisplayName }, Ui.Hint(subtitle) } } };
            check.Click += (_, _) =>
            {
                if (!_deliver.Remove(channel.Id))
                {
                    _deliver.Add(channel.Id);
                }

                _Render();
            };
            panel.Children.Add(check);
            if (on && channel.Targets.Count > 0)
            {
                var target = new ComboBox
                {
                    ItemsSource = channel.Targets.Select(candidate => candidate.DisplayName).ToList(),
                    SelectedIndex = Math.Max(0, channel.Targets.ToList().FindIndex(candidate => candidate.Id == _targets.GetValueOrDefault(channel.Id))),
                    Margin = new Thickness(26, 0, 0, 0),
                };
                target.SelectionChanged += (_, _) => _targets[channel.Id] = channel.Targets[Math.Max(0, target.SelectedIndex)].Id;
                panel.Children.Add(_Field("Send to", target));
            }
        }

        panel.Children.Add(new CheckBox
        {
            IsEnabled = false,
            Content = new StackPanel { Children = { new TextBlock { Text = "Save as note" }, Ui.Hint("Not available yet: writing a note to the project's Depot memory comes in a later version.") } },
        });
        panel.Children.Add(Ui.Note("These are the channels of the installed plugins. Mail, Telegram, Teams… appear here once their plugin is installed. Fallback: if a chosen medium's plugin is missing or sending fails, the message goes to the assistant, which passes it on through what it has. You'll see that in the run history.", warning: false));
        panel.Children.Add(new CheckBox
        {
            IsChecked = true,
            IsEnabled = false,
            Content = new StackPanel { Children = { new TextBlock { Text = "Deliver nothing when the result is empty" }, Ui.Hint("Always on: a run with nothing to say stays silent. It's still in the history.") } },
        });
        if (_deliver.Count == 0)
        {
            panel.Children.Add(Ui.Note("No delivery chosen — the result is only visible in the run history.", warning: true));
        }

        return panel;
    }

    // Held in _previewTask rather than awaited by the typing that started it; a failure is reported, never left unseen.
    private async Task _PreviewAsync()
    {
        var version = ++_previewVersion;
        SchedulesPreview? preview;
        try
        {
            preview = await _state.AskAsync<SchedulesPreview>(SchedulesChannel.Preview, new SchedulesPreviewRequest(Schedule, _zone.Trim()));
        }
        catch (Exception exception)
        {
            _Report(exception);
            return;
        }

        if (version != _previewVersion)
        {
            return;
        }

        IsScheduleValid = preview is { Valid: true };
        _previewText.Text = IsScheduleValid ? $"{preview?.ReadsAs} · {_zone}" : InvalidSchedule;
        _previewText.Themed(TextBlock.ForegroundProperty, IsScheduleValid ? "CockpitTextPrimaryBrush" : "CockpitStatusErrorBrush");
        _previewNext.Text = IsScheduleValid && preview is { Next.Count: > 0 } ? "Next: " + string.Join(" · ", preview.Next.Select(next => Ui.When(next))) : string.Empty;
        _save.IsEnabled = IsScheduleValid;
    }

    private async Task _TestRunAsync()
    {
        _host.ShowToast("Test run started: it runs once now with these settings, without saving. Delivery goes only to Cockpit.");
        var run = await _state.AskAsync<RunRecord>(SchedulesChannel.TestRun, new ScheduledTask
        {
            Id = _existing?.Id ?? "test",
            Name = string.IsNullOrWhiteSpace(_name) ? "Test run" : _name.Trim(),
            Schedule = Schedule,
            TimeZone = _zone,
            Action = new TaskAction(_kind, _text.Trim()) { Profile = _profile, WorkingDirectory = _folder.Trim().Length > 0 ? _folder.Trim() : null, Permission = _write ? AgentPermission.Write : AgentPermission.ReadOnly },
        });
        _host.ShowToast(run is null ? "The test run did not answer." : $"Test run: {run.Outcome}{(run.Reason is null ? string.Empty : $" — {run.Reason}")}", run?.Outcome == RunOutcome.Failed ? PluginToastSeverity.Error : PluginToastSeverity.Information);
    }

    // Back from a schedule string to the builder's fields, where one of its shapes fits exactly; else the cron view.
    private bool _ReadSchedule(string schedule)
    {
        var fields = schedule.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length == 3 && fields[0] == "once")
        {
            (_frequency, _date, _time) = ("once", fields[1], fields[2]);
            return true;
        }

        if (fields.Length == 3 && fields[0] == "every" && fields[2] == "m")
        {
            (_frequency, _every) = ("interval", fields[1]);
            return true;
        }

        if (fields.Length != 5 || !int.TryParse(fields[0], out var minute) || !int.TryParse(fields[1], out var hour) || fields[3] != "*")
        {
            (_cron, _cronText) = (true, schedule);
            return false;
        }

        _time = $"{hour:00}:{minute:00}";
        (_frequency, _days) = (fields[2], fields[4]) switch
        {
            ("*", "*") => ("daily", _days),
            ("1", "*") => ("monthly", _days),
            ("*", "1-5") => ("weekly", ["1", "2", "3", "4", "5"]),
            ("*", var list) when list.Split(',').All(day => Days.Any(known => known.Key == day)) => ("weekly", [.. list.Split(',')]),
            _ => ("cron", _days),
        };
        if (_frequency == "cron")
        {
            (_frequency, _cron, _cronText) = ("weekly", true, schedule);
            return false;
        }

        return true;
    }

    private (int Hour, int Minute) _Time()
    {
        var parts = _time.Split(':');
        return parts.Length == 2 && int.TryParse(parts[0], out var hour) && int.TryParse(parts[1], out var minute) ? (hour, minute) : (-1, -1);
    }

    private string _DayField()
    {
        var days = Days.Select(day => day.Key).Where(_days.Contains).ToList();
        // No day picked is no schedule: an empty field leaves four, which the preview refuses and Save with it.
        return days.SequenceEqual(["1", "2", "3", "4", "5"]) ? "1-5" : string.Join(",", days);
    }

    private void _Report(Exception exception) => _host.ShowToast($"Schedules: {exception.Message}", PluginToastSeverity.Error);

    private static Control _Heading(string number, string text) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        Children = { Ui.Tag(number), new TextBlock { Text = text, FontWeight = FontWeight.SemiBold } },
    };

    private static Control _Field(string label, Control value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*") };
        var key = Ui.Muted(label);
        key.VerticalAlignment = VerticalAlignment.Top;
        key.Margin = new Thickness(0, 6, 0, 0);
        grid.Children.Add(key);
        value.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        return grid;
    }

    private static Control _Option(bool selected, string title, string detail, Action pick)
    {
        var option = new RadioButton { IsChecked = selected, Content = new StackPanel { Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold }, Ui.Hint(detail) } } };
        option.Click += (_, _) => pick();
        return option;
    }

    private static TextBox _Text(string text, string placeholder, int lines, Action<string> changed)
    {
        var box = new TextBox { Text = text, PlaceholderText = placeholder, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = lines * 20, Width = 520 };
        box.TextChanged += (_, _) => changed(box.Text ?? string.Empty);
        return box;
    }
}
