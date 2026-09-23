using System.Globalization;

namespace Cockpit.Plugin.Workflows.Engine;

// When a scheduled flow is due (#69, #AC-1359). The forms are all things a person would actually write:
//   - `09:00` — every day at that time.
//   - `mon 09:00`, `mon,fri 09:00` — on the named weekday(s), at that time.
//   - `once 2026-10-01 09:00` — a single moment, never again.
//   - `every 15m`, `every 2h` — on the interval, counted from midnight so it lands on round numbers.
//
// Not cron. Cron is a language for a machine that must express "the third Tuesday", and this cockpit is one
// person's day: a time, a weekday, a single date or an interval covers it, and anything past that is better served
// by a shell command on a timer than by teaching everyone five asterisks.
internal static class Schedule
{
    // The latest scheduled moment at or before `now`, read in `zone`'s wall clock — or null when the schedule
    // cannot be read, or (for `once`) has not arrived yet. Never a guess: a schedule nobody can parse must never
    // fire, least of all every minute, so it returns null rather than the nearest thing that looked plausible.
    public static DateTimeOffset? Previous(string when, TimeZoneInfo zone, DateTimeOffset now)
    {
        var text = when.Trim();
        var local = TimeZoneInfo.ConvertTime(now, zone);

        if (text.StartsWith("once", StringComparison.OrdinalIgnoreCase))
        {
            return _Once(text[4..].Trim(), zone, now);
        }

        if (text.StartsWith("every", StringComparison.OrdinalIgnoreCase))
        {
            return _Interval(text[5..].Trim()) is { } interval && interval > TimeSpan.Zero
                ? _PreviousInterval(interval, zone, local)
                : null;
        }

        return _PreviousDayTime(text, zone, local);
    }

    // Resolves a "Time zone" trigger parameter to a zone — empty meaning the machine's own, so a flow written
    // before this parameter existed keeps firing exactly as it always did. An id this build cannot place never
    // resolves, and a schedule in a zone the clock cannot place must never fire.
    public static TimeZoneInfo? ResolveZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }

    private static DateTimeOffset? _Once(string text, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (!DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var when))
        {
            return null;
        }

        return _ToOffset(when, zone) is { } instant && instant <= now ? instant : null;
    }

    // A daily time, or a weekday time: split on the first space. No space at all is a bare `HH:mm`.
    private static DateTimeOffset? _PreviousDayTime(string text, TimeZoneInfo zone, DateTimeOffset local)
    {
        var spaceIndex = text.IndexOf(' ');
        if (spaceIndex < 0)
        {
            return TimeOnly.TryParseExact(text, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var daily)
                ? _PreviousDaily(daily, zone, local)
                : null;
        }

        var dayPart = text[..spaceIndex];
        var timePart = text[(spaceIndex + 1)..].Trim();

        if (!TimeOnly.TryParseExact(timePart, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            return null;
        }

        var days = new List<DayOfWeek>();
        foreach (var token in dayPart.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (_ParseDay(token.Trim()) is not { } day)
            {
                return null;
            }

            days.Add(day);
        }

        return days.Count > 0 ? _PreviousWeekday(days, time, zone, local) : null;
    }

    private static DateTimeOffset? _PreviousDaily(TimeOnly time, TimeZoneInfo zone, DateTimeOffset local)
    {
        var candidate = local.Date + time.ToTimeSpan();
        if (candidate > local.DateTime)
        {
            candidate = candidate.AddDays(-1);
        }

        return _ToOffset(candidate, zone);
    }

    private static DateTimeOffset? _PreviousWeekday(IReadOnlyList<DayOfWeek> days, TimeOnly time, TimeZoneInfo zone, DateTimeOffset local)
    {
        // Up to and including 7 days back: a single weekday checked before its own time on the day itself (a Monday
        // schedule, checked Monday morning) has no other candidate but last week's same day.
        for (var back = 0; back <= 7; back++)
        {
            var date = local.Date.AddDays(-back);
            if (!days.Contains(date.DayOfWeek))
            {
                continue;
            }

            var candidate = date + time.ToTimeSpan();
            if (back == 0 && candidate > local.DateTime)
            {
                continue;
            }

            return _ToOffset(candidate, zone);
        }

        return null;
    }

    private static DateTimeOffset? _PreviousInterval(TimeSpan interval, TimeZoneInfo zone, DateTimeOffset local)
    {
        var slots = Math.Floor(local.TimeOfDay.TotalMinutes / interval.TotalMinutes);
        var candidate = local.Date.AddMinutes(slots * interval.TotalMinutes);
        return _ToOffset(candidate, zone);
    }

    // A wall-clock moment in `zone`, converted to the instant it actually names. Null for the one moment a zone
    // cannot name at all — the hour a clock skips forward in spring. An hour a clock repeats in autumn resolves
    // deterministically to the standard-time side, so the same wall-clock moment always yields one instant.
    private static DateTimeOffset? _ToOffset(DateTime wallClock, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified))
        {
            return null;
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, zone), TimeSpan.Zero);
    }

    private static TimeSpan? _Interval(string text)
    {
        if (text.Length < 2)
        {
            return null;
        }

        var unit = text[^1];
        if (!int.TryParse(text[..^1].Trim(), CultureInfo.InvariantCulture, out var amount) || amount <= 0)
        {
            return null;
        }

        return char.ToLowerInvariant(unit) switch
        {
            'm' => TimeSpan.FromMinutes(amount),
            'h' => TimeSpan.FromHours(amount),
            _ => null,
        };
    }

    private static DayOfWeek? _ParseDay(string token) => token.ToLowerInvariant() switch
    {
        "sun" => DayOfWeek.Sunday,
        "mon" => DayOfWeek.Monday,
        "tue" => DayOfWeek.Tuesday,
        "wed" => DayOfWeek.Wednesday,
        "thu" => DayOfWeek.Thursday,
        "fri" => DayOfWeek.Friday,
        "sat" => DayOfWeek.Saturday,
        _ => null,
    };
}
