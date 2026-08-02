using System.Globalization;

namespace Cockpit.Core.Diagnostics;

// One line of `ps -axo pid=,ppid=,time=,rss=` — how macOS's process table is read (#78). Pure, and tested,
// because it is the only part of the macOS path this codebase can verify without a Mac: the parsing is where it
// would go wrong, and the time format is the trap (`MM:SS.ss`, or `HH:MM:SS` once a process has run
// for an hour, or `D-HH:MM:SS` after a day).
public static class PsLine
{
    public static ProcessRow? Parse(string line)
    {
        // The name is asked for last (comm=) and taken as the whole rest of the line: a command may contain spaces,
        // and splitting it into fields would cut a path like "/Applications/LM Studio.app/..." in half.
        var fields = line.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 4)
        {
            return null;
        }

        if (!int.TryParse(fields[0], out var processId)
            || !int.TryParse(fields[1], out var parentProcessId)
            || ParseCpuTime(fields[2]) is not { } cpuTime
            || !long.TryParse(fields[3], out var residentKilobytes))
        {
            return null;
        }

        var name = fields.Length > 4 ? Path.GetFileName(fields[4].Trim()) : string.Empty;

        return new ProcessRow(processId, parentProcessId, cpuTime, residentKilobytes * 1024, name);
    }

    // ps writes accumulated CPU time as `MM:SS.ss`, `HH:MM:SS` or `D-HH:MM:SS` depending on how long the process has lived.
    public static TimeSpan? ParseCpuTime(string value)
    {
        var days = 0;
        var rest = value;

        var dash = value.IndexOf('-');
        if (dash > 0)
        {
            if (!int.TryParse(value[..dash], out days))
            {
                return null;
            }

            rest = value[(dash + 1)..];
        }

        var parts = rest.Split(':');
        if (parts.Length is < 2 or > 3)
        {
            return null;
        }

        var hours = 0;
        var index = 0;
        if (parts.Length == 3)
        {
            if (!int.TryParse(parts[0], out hours))
            {
                return null;
            }

            index = 1;
        }

        if (!int.TryParse(parts[index], out var minutes)
            || !double.TryParse(parts[index + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return null;
        }

        return TimeSpan.FromDays(days) + TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
    }
}
