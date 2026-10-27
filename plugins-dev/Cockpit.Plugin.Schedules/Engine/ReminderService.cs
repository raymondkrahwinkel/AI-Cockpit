using System.Globalization;
using Cockpit.Plugin.Schedules.Contracts;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Notifications;

namespace Cockpit.Plugin.Schedules.Engine;

// Reminders on the engine's own tick (AC-1549): at its moment a reminder becomes due, written before it is said, so a
// restart within the minute never says it twice; one missed while the machine slept goes off when it is back, however
// late. It goes off in the backend part, as a cockpit toast with Done · Snooze and on every chosen channel.
internal sealed class ReminderService(ICockpitHost host, IPluginStorage storage, TaskDelivery delivery, TimeProvider time, Action<int>? dueChanged = null)
{
    public const string DoneLabel = "Done";
    public const string SnoozeShortLabel = "Snooze 10 min";
    public const string SnoozeLongLabel = "1 hour";

    private const string Key = "reminders";
    private static readonly TimeSpan OnTime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SnoozeShort = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SnoozeLong = TimeSpan.FromHours(1);
    private readonly Lock _gate = new();

    public IReadOnlyList<Reminder> Load() => StoreJson.Read<List<Reminder>>(storage.Get<string>(Key)) ?? [];

    public (Reminder? Reminder, string? Error) Create(string text, string schedule, string timeZone, IReadOnlyList<DeliveryTarget> deliverTo, string origin)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, "A reminder needs a text.");
        }

        if (_Next(schedule, timeZone, time.GetUtcNow() - OnTime) is not { } slot)
        {
            return (null, $"'{schedule}' in '{timeZone}' is no moment still to come.");
        }

        var reminder = new Reminder
        {
            Id = Guid.NewGuid().ToString("n"),
            Text = text.Trim(),
            Schedule = schedule.Trim(),
            TimeZone = timeZone,
            Slot = slot,
            Delivery = deliverTo.Count == 0 ? [new DeliveryTarget(DeliveryTarget.CockpitNotification)] : deliverTo,
            Origin = origin,
        };
        _Change(all => [.. all, reminder]);
        return (reminder, null);
    }

    // Every open reminder whose moment has come, due before it is said; the returned task completes once all are said.
    public Task TickAsync(DateTimeOffset now)
    {
        List<Reminder> going = [];
        if (Load().All(reminder => reminder.State != ReminderState.Open || reminder.Due > now))
        {
            // Nothing goes off: the store stays as it is, and the badge hears the count it already had.
            dueChanged?.Invoke(Load().Count(reminder => reminder.State == ReminderState.Due));
            return Task.CompletedTask;
        }

        _Change(all => [.. all.Select(reminder =>
        {
            if (reminder.State != ReminderState.Open || reminder.Due > now)
            {
                return reminder;
            }

            var due = reminder with { State = ReminderState.Due, FiredAt = now };
            going.Add(due);
            return due;
        })]);
        return Task.WhenAll(going.Select(reminder => _SayAsync(reminder, now)));
    }

    // Done for this occurrence: a repeating one opens on its next moment after this one and after now, a one-off is done.
    public Reminder? Complete(string id, DateTimeOffset? occurrence = null) => _Update(id, occurrence, reminder =>
    {
        var now = time.GetUtcNow();
        var done = (IReadOnlyList<DateTimeOffset>)[reminder.Slot, .. reminder.Done.Take(19)];
        if (reminder.Repeats && _Next(reminder.Schedule, reminder.TimeZone, reminder.Slot > now ? reminder.Slot : now) is { } next)
        {
            return reminder with { State = ReminderState.Open, Slot = next, SnoozedUntil = null, Done = done };
        }

        return reminder with { State = ReminderState.Done, SnoozedUntil = null, Done = done };
    });

    public Reminder? SnoozeBy(string id, TimeSpan by) => Snooze(id, time.GetUtcNow() + by);

    public Reminder? Snooze(string id, DateTimeOffset until, DateTimeOffset? occurrence = null) =>
        _Update(id, occurrence, reminder => reminder with { State = ReminderState.Open, SnoozedUntil = until });

    public bool Delete(string id)
    {
        var found = false;
        _Change(all =>
        {
            found = all.Any(reminder => reminder.Id == id);
            return [.. all.Where(reminder => reminder.Id != id)];
        });
        return found;
    }

    // A click or a reply from any door: the toast, a channel's button, or its text. It answers the one time the reminder
    // went off that it came with: after a snooze brought it back, the old Discord button is spent.
    public Reminder? Answer(string id, DateTimeOffset occurrence, DateTimeOffset firedAt, string choice)
    {
        if (Load().FirstOrDefault(reminder => reminder.Id == id) is not { State: ReminderState.Due } current || current.FiredAt != firedAt)
        {
            return null;
        }

        return choice switch
        {
            DoneLabel => Complete(id, occurrence),
            SnoozeShortLabel => Snooze(id, time.GetUtcNow() + SnoozeShort, occurrence),
            SnoozeLongLabel => Snooze(id, time.GetUtcNow() + SnoozeLong, occurrence),
            _ => null,
        };
    }

    // AC-1804: the cockpit's kept toast after a restart: still due from the same firing gets its buttons back.
    public PluginNotification? Recheck(string key) =>
        Load().FirstOrDefault(reminder => reminder.State == ReminderState.Due && _FiringKey(reminder, reminder.FiredAt ?? default) == key) is { } due
            ? _Notification(due, time.GetUtcNow())
            : null;

    private async Task _SayAsync(Reminder reminder, DateTimeOffset now)
    {
        var (text, _) = _Said(reminder, now);
        var occurrence = reminder.Slot;
        var firedAt = reminder.FiredAt ?? now;

        if (reminder.Delivery.Any(target => target.Channel == DeliveryTarget.CockpitNotification))
        {
            host.Notify(_Notification(reminder, now));
        }

        var message = new OperatorMessage
        {
            Text = text,
            CorrelationId = _FiringKey(reminder, firedAt),
            Question = new AssistantChannelQuestion
            {
                Question = reminder.Text,
                AllowOther = false,
                Options = [new AssistantChannelOption(DoneLabel), new AssistantChannelOption(SnoozeShortLabel), new AssistantChannelOption(SnoozeLongLabel)],
            },
        };
        var (notes, failed) = await delivery.SendToChannelsAsync(reminder.Delivery, message, answer =>
        {
            Answer(reminder.Id, occurrence, firedAt, answer.Chosen.FirstOrDefault() ?? string.Empty);
            return Task.CompletedTask;
        }, CancellationToken.None);
        // What this time came to, every time: a note from an earlier week never stands for this one.
        _Update(reminder.Id, occurrence, current => current with { DeliveryNote = failed ?? notes });
    }

    private PluginNotification _Notification(Reminder reminder, DateTimeOffset now)
    {
        var (text, was) = _Said(reminder, now);
        var occurrence = reminder.Slot;
        var firedAt = reminder.FiredAt ?? now;
        var elsewhere = reminder.Delivery.Where(target => target.Channel != DeliveryTarget.CockpitNotification).Select(target => _ChannelName(target.Channel)).Distinct().ToList();
        var detail = $"Reminder · {was}{(reminder.Repeats ? $" · {CronText.Describe(reminder.Schedule) ?? "repeats"}" : string.Empty)}"
            + (elsewhere.Count == 0 ? "." : $". Also sent via {string.Join(" and ", elsewhere)}.");
        return new PluginNotification(_FiringKey(reminder, firedAt), $"{text}\n{detail}", PluginToastSeverity.Warning,
        [
            new PluginToastAction(DoneLabel, () => _AnswerFromToast(reminder.Id, occurrence, firedAt, DoneLabel)),
            new PluginToastAction(SnoozeShortLabel, () => _AnswerFromToast(reminder.Id, occurrence, firedAt, SnoozeShortLabel)),
            new PluginToastAction(SnoozeLongLabel, () => _AnswerFromToast(reminder.Id, occurrence, firedAt, SnoozeLongLabel)),
        ]);
    }

    private static (string Text, string Was) _Said(Reminder reminder, DateTimeOffset now)
    {
        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(reminder.TimeZone, out var found) ? found : TimeZoneInfo.Utc;
        var was = TimeZoneInfo.ConvertTime(reminder.Due, zone).ToString("HH:mm", CultureInfo.InvariantCulture);
        return (now - reminder.Due > OnTime ? $"⏰ {reminder.Text} · missed, was {was}" : $"⏰ {reminder.Text} · {was}", was);
    }

    // One firing of one reminder: a snooze that brings it back is a new one, and the old one is resolved.
    private static string _FiringKey(Reminder reminder, DateTimeOffset firedAt) => $"reminder-{reminder.Id}-{firedAt.UtcTicks}";

    // On the cockpit's UI thread: a failure here is said, and the toast still closes behind it.
    private void _AnswerFromToast(string id, DateTimeOffset occurrence, DateTimeOffset firedAt, string choice)
    {
        try
        {
            Answer(id, occurrence, firedAt, choice);
        }
        catch (Exception exception)
        {
            host.ShowToast($"The reminder could not take \"{choice}\": {exception.Message}", PluginToastSeverity.Error);
        }
    }

    private string _ChannelName(string channel) =>
        host.OperatorMessageChannels.FirstOrDefault(open => open.Id == channel)?.DisplayName
            ?? (channel.Length > 0 ? $"{char.ToUpperInvariant(channel[0])}{channel[1..]}" : channel);

    // An answer to an occurrence that is no longer this one (already ticked off or moved on) changes nothing.
    private Reminder? _Update(string id, DateTimeOffset? occurrence, Func<Reminder, Reminder> change)
    {
        Reminder? changed = null;
        _Change(all => [.. all.Select(reminder =>
        {
            if (reminder.Id != id || (occurrence is { } slot && reminder.Slot != slot) || reminder.State == ReminderState.Done)
            {
                return reminder;
            }

            changed = change(reminder);
            return changed;
        })]);
        return changed;
    }

    private void _Change(Func<IReadOnlyList<Reminder>, IReadOnlyList<Reminder>> change)
    {
        int due;
        lock (_gate)
        {
            var changed = change(Load());
            storage.Set(Key, StoreJson.Write(changed));
            due = changed.Count(reminder => reminder.State == ReminderState.Due);
        }

        dueChanged?.Invoke(due);
    }

    private static DateTimeOffset? _Next(string schedule, string timeZone, DateTimeOffset after) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var zone) && TaskSchedule.TryParse(schedule, zone) is { } parsed
            ? parsed.Slots(after, after.AddYears(5), zone).Cast<DateTimeOffset?>().FirstOrDefault()
            : null;
}
