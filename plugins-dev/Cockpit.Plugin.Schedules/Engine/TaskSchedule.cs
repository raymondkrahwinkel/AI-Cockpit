using System.Globalization;
using System.Text.RegularExpressions;
using Cronos;

namespace Cockpit.Plugin.Schedules.Engine;

// A task's schedule, parsed: a 5-field cron evaluated by Cronos in the task's zone, `once yyyy-MM-dd HH:mm` in that
// zone, or `every N m` on the UTC minute grid. Cronos owns the daylight-saving rules: a skipped wall time moves
// forward and a repeated one fires once.
internal sealed partial class TaskSchedule
{
    private readonly CronExpression? _cron;
    private readonly DateTimeOffset? _once;
    private readonly TimeSpan? _every;

    private TaskSchedule(CronExpression? cron, DateTimeOffset? once, TimeSpan? every)
    {
        _cron = cron;
        _once = once;
        _every = every;
    }

    public static TaskSchedule? TryParse(string schedule, TimeZoneInfo zone)
    {
        var text = schedule.Trim();
        if (Once().Match(text) is { Success: true } once)
        {
            if (!DateTime.TryParseExact(once.Groups[1].Value, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
                || zone.IsInvalidTime(local))
            {
                return null;
            }

            return new TaskSchedule(null, new DateTimeOffset(local, zone.GetUtcOffset(local)), null);
        }

        if (Every().Match(text) is { Success: true } every)
        {
            return int.TryParse(every.Groups[1].Value, CultureInfo.InvariantCulture, out var minutes) && minutes > 0
                ? new TaskSchedule(null, null, TimeSpan.FromMinutes(minutes))
                : null;
        }

        return CronExpression.TryParse(text, CronFormat.Standard, out var cron) ? new TaskSchedule(cron, null, null) : null;
    }

    // The slots after `after` up to and including `until`, oldest first.
    public IEnumerable<DateTimeOffset> Slots(DateTimeOffset after, DateTimeOffset until, TimeZoneInfo zone)
    {
        if (_cron is not null)
        {
            return after >= until ? [] : _cron.GetOccurrences(after, until, zone, fromInclusive: false, toInclusive: true);
        }

        if (_once is { } once)
        {
            return once > after && once <= until ? [once] : [];
        }

        return _EverySlots(after, until);
    }

    private IEnumerable<DateTimeOffset> _EverySlots(DateTimeOffset after, DateTimeOffset until)
    {
        var step = _every ?? throw new InvalidOperationException("Not an interval schedule.");
        var ticks = (after.UtcTicks / step.Ticks + 1) * step.Ticks;
        for (var slot = new DateTimeOffset(ticks, TimeSpan.Zero); slot <= until; slot += step)
        {
            yield return slot;
        }
    }

    [GeneratedRegex(@"^once\s+(\d{4}-\d{2}-\d{2} \d{2}:\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex Once();

    [GeneratedRegex(@"^every\s+(\d+)\s*m(?:in(?:ute)?s?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Every();
}
