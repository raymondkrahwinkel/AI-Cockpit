namespace Cockpit.Plugin.Schedules.Engine;

// A task's mark, with the schedule and zone it was counted under: a mark from another schedule says nothing.
internal sealed record TaskMark(DateTimeOffset At, string Schedule, string TimeZone);
