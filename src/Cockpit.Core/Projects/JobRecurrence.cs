namespace Cockpit.Core.Projects;

// AC-493: how often a job comes round — the nth weekday of the month, which is the floor Raymond's own example
// asks for ("every second Monday of the month I have to process the invoices"). Deliberately not a cron
// expression: a second scheduling language next to Autopilot's is the thing this ticket exists to avoid.
public sealed record JobRecurrence(int WeekOfMonth, DayOfWeek DayOfWeek)
{
    // Clamped rather than refused, because a hand-edited config can write any number here and a project that
    // fails to load is a worse answer than a reminder landing on the nearest week that exists.
    public int WeekOfMonth { get; init; } = Math.Clamp(WeekOfMonth, 1, 5);

    // The rule in words — "second Monday of the month". Only what the rule itself says: whether the work was done
    // is not something a rule can know, so nothing here is phrased as if it did.
    public string Describe() => $"{_Ordinals[WeekOfMonth - 1]} {DayOfWeek} of the month";

    // The most recent occurrence on or before `today`. Read from the rule and never from the day the cockpit
    // happened to open: a monthly task whose date quietly moves up to today is a period nobody knows was
    // skipped, which is the whole of criterion 3.
    public DateOnly LastOccurrenceOn(DateOnly today)
    {
        for (var month = new DateOnly(today.Year, today.Month, 1); ; month = month.AddMonths(-1))
        {
            if (_OccurrenceIn(month) is { } occurrence && occurrence <= today)
            {
                return occurrence;
            }
        }
    }

    // The rule's day within `firstOfMonth`'s month, or null when that month has no such week — a fifth Monday
    // does not exist every month, and the caller walks back to a month that has one.
    private DateOnly? _OccurrenceIn(DateOnly firstOfMonth)
    {
        var offset = ((int)DayOfWeek - (int)firstOfMonth.DayOfWeek + 7) % 7;
        var occurrence = firstOfMonth.AddDays(offset + ((WeekOfMonth - 1) * 7));
        return occurrence.Month == firstOfMonth.Month ? occurrence : null;
    }

    private static readonly string[] _Ordinals = ["first", "second", "third", "fourth", "fifth"];
}
