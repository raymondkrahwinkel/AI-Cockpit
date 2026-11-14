using Avalonia.Controls;
using Avalonia.Layout;
using Cockpit.Plugin.Schedules.Contracts;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Schedules.UI;

internal sealed class RemindersView : UserControl
{
    private readonly SchedulesState _state;
    private readonly ICockpitUiHost _host;
    private readonly StackPanel _rows = new() { Spacing = 12 };
    private readonly TextBox _text = new() { PlaceholderText = "Reminder" };
    private readonly TextBox _schedule = new() { PlaceholderText = "once yyyy-MM-dd HH:mm or cron" };
    private readonly TextBox _zone;

    public RemindersView(SchedulesState state, ICockpitUiHost host)
    {
        _state = state;
        _host = host;
        _zone = new TextBox { Text = state.Settings.DefaultTimeZone, PlaceholderText = "Time zone" };
        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Ui.Title("Reminders"), _text, _schedule, _zone,
                Ui.AsyncButton("Create reminder", _CreateAsync, _Report, "Accent"),
                new ScrollViewer { Content = _rows },
            },
        };
        state.Changed += _Render;
        _Render();
    }

    private async Task _CreateAsync()
    {
        await _ChangeAsync(SchedulesChannel.CreateReminder, new Reminder
        {
            Id = string.Empty, Text = _text.Text ?? string.Empty,
            Schedule = _schedule.Text ?? string.Empty, TimeZone = _zone.Text ?? string.Empty,
            Slot = default,
        });
    }

    private async Task _ChangeAsync(string action, object request)
    {
        var result = await _state.AskAsync<SchedulesResult>(action, request);
        if (result is not { Ok: true })
        {
            _host.ShowToast(result?.Error ?? "The reminder could not be changed.", PluginToastSeverity.Error);
            return;
        }
        await _state.RefreshAsync();
    }

    private void _Render()
    {
        _rows.Children.Clear();
        foreach (var reminder in _state.Reminders)
        {
            var row = new StackPanel { Spacing = 4 };
            row.Children.Add(Ui.Muted(reminder.Text));
            row.Children.Add(Ui.Hint($"{reminder.State} · {reminder.Due:u} · {reminder.TimeZone}"));
            if (reminder is { State: ReminderState.Due, FiredAt: { } firedAt })
            {
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                foreach (var choice in new[] { "Done", "Snooze 10 min", "1 hour" })
                {
                    actions.Children.Add(Ui.AsyncButton(choice, () => _ChangeAsync(SchedulesChannel.AnswerReminder,
                        new ReminderActionRequest(reminder.Id, reminder.Slot, firedAt, choice)), _Report));
                }
                row.Children.Add(actions);
            }
            _rows.Children.Add(row);
        }
    }

    private void _Report(Exception exception) => _host.ShowToast($"Reminders: {exception.Message}", PluginToastSeverity.Error);
}
