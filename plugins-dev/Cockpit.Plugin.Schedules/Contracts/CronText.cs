using System.Globalization;
using System.Text.RegularExpressions;

namespace Cockpit.Plugin.Schedules.Contracts;

// Cron and plain English both ways, for the schedule builder (plan §4): the builder writes cron, and a cron typed by
// hand reads back as English where one of these shapes fits. Anything else is null, which the UI shows as "Custom".
public static partial class CronText
{
    private static readonly string[] Days = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    private static readonly string[] Ordinals = ["First", "Second", "Third", "Fourth"];

    public static string? Describe(string cron)
    {
        var fields = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5 || fields[3] != "*")
        {
            return null;
        }

        var (minute, hour, day, weekday) = (fields[0], fields[1], fields[2], fields[4]);
        if (minute.StartsWith("*/", StringComparison.Ordinal) && _Number(minute[2..], 1, 59) is { } every
            && (hour, day, weekday) == ("*", "*", "*"))
        {
            return $"Every {every} minutes";
        }

        if (_Number(minute, 0, 59) is not { } m)
        {
            return null;
        }

        if ((hour, day, weekday) == ("*", "*", "*"))
        {
            return $"Every hour at :{m:00}";
        }

        if (_Number(hour, 0, 23) is not { } h)
        {
            return null;
        }

        var at = $"at {h:00}:{m:00}";
        if (day != "*")
        {
            return weekday == "*" && _Number(day, 1, 31) is { } dayOfMonth ? $"On day {dayOfMonth} of every month {at}" : null;
        }

        if (weekday == "*")
        {
            return $"Every day {at}";
        }

        if (weekday == "1-5")
        {
            return $"Every weekday {at}";
        }

        if (NthWeekday().Match(weekday) is { Success: true } nth)
        {
            return $"{Ordinals[int.Parse(nth.Groups[2].Value, CultureInfo.InvariantCulture) - 1]} {Days[int.Parse(nth.Groups[1].Value, CultureInfo.InvariantCulture)]} of the month {at}";
        }

        var names = weekday.Split(',').Select(part => _Number(part, 0, 6)).ToList();
        if (names.Any(name => name is null))
        {
            return null;
        }

        return $"Every {_JoinDays(names.OfType<int>().Select(index => Days[index]).ToList())} {at}";
    }

    public static string? Parse(string text)
    {
        var trimmed = text.Trim();
        if (EveryMinutes().Match(trimmed) is { Success: true } minutes)
        {
            return $"*/{minutes.Groups[1].Value} * * * *";
        }

        if (EveryHour().Match(trimmed) is { Success: true } hourly)
        {
            return $"{int.Parse(hourly.Groups[1].Value, CultureInfo.InvariantCulture)} * * * *";
        }

        if (AtTime().Match(trimmed) is not { Success: true } timed)
        {
            return null;
        }

        var time = $"{int.Parse(timed.Groups[3].Value, CultureInfo.InvariantCulture)} {int.Parse(timed.Groups[2].Value, CultureInfo.InvariantCulture)}";
        var when = timed.Groups[1].Value.Trim();
        if (when.Equals("Every day", StringComparison.OrdinalIgnoreCase))
        {
            return $"{time} * * *";
        }

        if (when.Equals("Every weekday", StringComparison.OrdinalIgnoreCase))
        {
            return $"{time} * * 1-5";
        }

        if (DayOfMonth().Match(when) is { Success: true } monthly)
        {
            return $"{time} {monthly.Groups[1].Value} * *";
        }

        if (NthOfMonth().Match(when) is { Success: true } nth && _Day(nth.Groups[2].Value) is { } nthDay)
        {
            var ordinal = Array.FindIndex(Ordinals, candidate => candidate.Equals(nth.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) + 1;
            return $"{time} * * {nthDay}#{ordinal}";
        }

        if (!when.StartsWith("Every ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var days = DaySeparator().Split(when[6..]).Select(_Day).ToList();
        return days.Any(day => day is null) ? null : $"{time} * * {string.Join(',', days)}";
    }

    private static int? _Number(string field, int min, int max) =>
        int.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max ? value : null;

    private static int? _Day(string name)
    {
        var index = Array.FindIndex(Days, day => day.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        return index < 0 ? null : index;
    }

    private static string _JoinDays(IReadOnlyList<string> days) =>
        days.Count == 1 ? days[0] : $"{string.Join(", ", days.Take(days.Count - 1))} and {days[^1]}";

    [GeneratedRegex(@"^([0-6])#([1-4])$")]
    private static partial Regex NthWeekday();

    [GeneratedRegex(@"^every ([1-9]|[1-5]\d) minutes$", RegexOptions.IgnoreCase)]
    private static partial Regex EveryMinutes();

    [GeneratedRegex(@"^every hour at :([0-5]\d)$", RegexOptions.IgnoreCase)]
    private static partial Regex EveryHour();

    [GeneratedRegex(@"^(.+) at ([01]\d|2[0-3]):([0-5]\d)$", RegexOptions.IgnoreCase)]
    private static partial Regex AtTime();

    [GeneratedRegex(@"^on day ([1-9]|[12]\d|3[01]) of every month$", RegexOptions.IgnoreCase)]
    private static partial Regex DayOfMonth();

    [GeneratedRegex(@"^(first|second|third|fourth) (\w+) of the month$", RegexOptions.IgnoreCase)]
    private static partial Regex NthOfMonth();

    [GeneratedRegex(@",\s*|\s+and\s+", RegexOptions.IgnoreCase)]
    private static partial Regex DaySeparator();
}
