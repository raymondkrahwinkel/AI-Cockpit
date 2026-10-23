using NSubstitute;
using Cockpit.Plugin.Schedules.Contracts;
using Cockpit.Plugin.Schedules.Engine;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Notifications;

namespace Cockpit.Plugin.Schedules.Tests;

// AC-1549, layer 2 on a held clock: reminders go off in the backend part, so no screen is in these tests at all, which
// is criterion 2 (closed or hidden, it goes off the same). Amsterdam is UTC+2 until 25 October 2026 and UTC+1 after it.
public class ReminderServiceTests
{
    // Criteria 1, 5 and 6: "every Monday 09:00" goes off at 09:00 as a toast with Done · Snooze and as a Discord message
    // with the same options; Snooze 10 min from Discord brings it back at 09:10, a restart in that minute says it once,
    // and Done on the toast opens next Monday with today's in Done.
    [Fact]
    public async Task EveryMondayAtNine_GoesOffAtNine_SnoozesToTen_PastNine_SaysItOnce_AndDoneOpensNextMonday()
    {
        var (host, storage, clock, toasts, messages) = _Host(new DateTimeOffset(2026, 10, 19, 6, 59, 30, TimeSpan.Zero));
        var reminders = new ReminderService(host, storage, new TaskDelivery(host), clock);
        var reminder = reminders.Create("Water the plants", "0 9 * * 1", "Europe/Amsterdam", [new DeliveryTarget(DeliveryTarget.CockpitNotification), new DeliveryTarget("discord", "dm")], "Created by you").Reminder
            ?? throw new InvalidOperationException("Not created.");

        clock.Now = new DateTimeOffset(2026, 10, 19, 7, 0, 10, TimeSpan.Zero);
        await reminders.TickAsync(clock.Now);
        var (_, answer) = Assert.Single(messages);
        await answer(new OperatorMessageAnswer("reminder", "111", [ReminderService.SnoozeShortLabel]));
        var snoozedTo = reminders.Load().Single().Due;
        clock.Now = new DateTimeOffset(2026, 10, 19, 7, 5, 0, TimeSpan.Zero);
        await reminders.TickAsync(clock.Now);
        clock.Now = new DateTimeOffset(2026, 10, 19, 7, 10, 10, TimeSpan.Zero);
        await reminders.TickAsync(clock.Now);
        await new ReminderService(host, storage, new TaskDelivery(host), clock).TickAsync(clock.Now.AddSeconds(10));
        toasts[^1].Actions[0].OnInvoke();
        var after = reminders.Load().Single();

        Assert.Equal(2, toasts.Count);
        Assert.All(toasts, toast =>
        {
            Assert.Equal([ReminderService.DoneLabel, ReminderService.SnoozeShortLabel, ReminderService.SnoozeLongLabel], toast.Actions.Select(action => action.Label));
            Assert.True(toast.Sticky);
        });
        Assert.Equal([ReminderService.DoneLabel, ReminderService.SnoozeShortLabel, ReminderService.SnoozeLongLabel], messages[0].Message.Question?.Options.Select(option => option.Label));
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 7, 10, 10, TimeSpan.Zero), snoozedTo);
        Assert.Equal((ReminderState.Open, new DateTimeOffset(2026, 10, 26, 8, 0, 0, TimeSpan.Zero)), (after.State, after.Slot));
        Assert.Equal([reminder.Slot], after.Done);
    }

    // Criteria 3, 4 and 5's counter-test: asleep from 08:00 until 11:42 the next day (27 hours, past AI-Hub's 24), the
    // 09:00 reminders still go off, "missed, was 09:00"; Slack has no plugin, so it is handed to the assistant, while
    // Discord, which has one, goes straight; and a one-off ticked off is done, with no next one.
    [Fact]
    public async Task AMissedReminder_GoesOffWhenBack_AChannelWithoutPlugin_GoesToTheAssistant_AndAOneOffEndsDone()
    {
        var (host, storage, clock, toasts, messages) = _Host(new DateTimeOffset(2026, 10, 22, 6, 0, 0, TimeSpan.Zero));
        host.NotifyAssistantAsync(TaskDelivery.AssistantKind, Arg.Any<string>()).Returns(true);
        var reminders = new ReminderService(host, storage, new TaskDelivery(host), clock);
        var cockpit = new DeliveryTarget(DeliveryTarget.CockpitNotification);
        var slack = reminders.Create("Call the garage", "once 2026-10-22 09:00", "Europe/Amsterdam", [cockpit, new DeliveryTarget("slack")], "Created by you").Reminder
            ?? throw new InvalidOperationException("Not created.");
        var discord = reminders.Create("Pay the invoice", "once 2026-10-22 09:00", "Europe/Amsterdam", [cockpit, new DeliveryTarget("discord", "dm")], "Created by you").Reminder
            ?? throw new InvalidOperationException("Not created.");

        clock.Now = new DateTimeOffset(2026, 10, 23, 9, 42, 0, TimeSpan.Zero);
        await reminders.TickAsync(clock.Now);
        reminders.Complete(slack.Id);
        var after = reminders.Load().ToDictionary(reminder => reminder.Id);

        Assert.Equal(2, toasts.Count);
        Assert.All(toasts, toast => Assert.Contains("missed, was 09:00", toast.Message, StringComparison.Ordinal));
        Assert.Equal("Slack: plugin not installed → handed to the assistant ✓", after[slack.Id].DeliveryNote);
        Assert.Null(after[discord.Id].DeliveryNote);
        Assert.Single(messages);
        await host.Received(1).NotifyAssistantAsync(TaskDelivery.AssistantKind, Arg.Any<string>());
        Assert.Equal((ReminderState.Done, slack.Slot), (after[slack.Id].State, after[slack.Id].Slot));
    }

    private sealed record Toast(string Message, IReadOnlyList<PluginToastAction> Actions, bool Sticky);

    private static (ICockpitHost Host, InMemoryPluginStorage Storage, TestClock Clock, List<Toast> Toasts, List<(OperatorMessage Message, Func<OperatorMessageAnswer, Task> Answer)> Messages) _Host(DateTimeOffset start)
    {
        var host = Substitute.For<ICockpitHost>();
        var toasts = new List<Toast>();
        var messages = new List<(OperatorMessage, Func<OperatorMessageAnswer, Task>)>();
        host.OperatorMessageChannels.Returns([new OperatorMessageChannelInfo("discord", "Discord", [new OperatorMessageTarget("dm", "Direct message to you")])]);
        host.When(cockpit => cockpit.ShowToast(Arg.Any<string>(), Arg.Any<PluginToastSeverity>(), Arg.Any<IReadOnlyList<PluginToastAction>>(), Arg.Any<bool>()))
            .Do(call => toasts.Add(new Toast(call.ArgAt<string>(0), call.ArgAt<IReadOnlyList<PluginToastAction>>(2), call.ArgAt<bool>(3))));
        host.SendOperatorMessageAsync("discord", "dm", Arg.Any<OperatorMessage>(), Arg.Any<Func<OperatorMessageAnswer, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                messages.Add((call.ArgAt<OperatorMessage>(2), call.ArgAt<Func<OperatorMessageAnswer, Task>?>(3) ?? (_ => Task.CompletedTask)));
                return OperatorMessageResult.Sent();
            });
        return (host, new InMemoryPluginStorage(), new TestClock(start), toasts, messages);
    }
}
