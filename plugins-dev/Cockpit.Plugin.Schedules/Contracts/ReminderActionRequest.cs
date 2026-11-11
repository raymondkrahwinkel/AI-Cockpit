namespace Cockpit.Plugin.Schedules.Contracts;

internal sealed record ReminderActionRequest(string Id, DateTimeOffset Slot, DateTimeOffset FiredAt, string Choice);
