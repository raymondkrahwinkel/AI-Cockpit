using System.Globalization;

namespace Cockpit.Plugin.Workflows.Engine;

/// <summary>
/// When a scheduled flow is due (#69). Two forms, both of them things a person would actually write:
/// <list type="bullet">
///   <item><c>09:00</c> — every day at that time.</item>
///   <item><c>every 15m</c>, <c>every 2h</c> — on the interval, counted from midnight so it lands on round numbers.</item>
/// </list>
/// <para>
/// Not cron. Cron is a language for a machine that must express "the third Tuesday", and this cockpit is one person's
/// day: a time or an interval covers it, and anything it does not cover is better served by a shell command on a
/// timer than by teaching everyone five asterisks.
/// </para>
/// </summary>
internal static class Schedule
{
    /// <summary>Whether a flow written as <paramref name="when"/> should fire in the minute <paramref name="now"/> falls in. False for anything unreadable — a schedule nobody can parse must never fire, least of all every minute.</summary>
    public static bool IsDue(string when, DateTimeOffset now)
    {
        var text = when.Trim();

        if (text.StartsWith("every", StringComparison.OrdinalIgnoreCase))
        {
            return _Interval(text[5..].Trim()) is { } interval
                && interval > TimeSpan.Zero
                && Math.Abs(now.TimeOfDay.TotalMinutes % interval.TotalMinutes) < 0.5;
        }

        // A time of day: due in the minute it names.
        return TimeOnly.TryParseExact(text, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            && time.Hour == now.Hour
            && time.Minute == now.Minute;
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
}
