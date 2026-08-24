namespace Cockpit.Core.Diagnostics;

// AC-1013 (was #78): One line of Linux's `/proc/&lt;pid&gt;/stat` — parent pid and CPU ticks. Field 2 (the exe
// name) is parenthesized and may itself contain spaces/parentheses (e.g. "my prog (v2)"), so counting fields
// from the left is wrong; the reliable trick is counting after the LAST ')'.
public sealed record ProcStatLine(string Name, int ParentProcessId, long UserTicks, long SystemTicks)
{
    public long TotalTicks => UserTicks + SystemTicks;

    public static ProcStatLine? Parse(string line)
    {
        var firstParenthesis = line.IndexOf('(');
        var lastParenthesis = line.LastIndexOf(')');
        if (firstParenthesis < 0 || lastParenthesis < firstParenthesis || lastParenthesis + 2 >= line.Length)
        {
            return null;
        }

        // The name is what is between the parentheses — all of it, including any spaces and parentheses of its own,
        // which is the same trap that makes counting fields from the left wrong.
        var name = line[(firstParenthesis + 1)..lastParenthesis];

        // After the ')' the fields are: state(3) ppid(4) pgrp(5) session(6) tty(7) tpgid(8) flags(9)
        // minflt(10) cminflt(11) majflt(12) cmajflt(13) utime(14) stime(15) ...
        var fields = line[(lastParenthesis + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 13)
        {
            return null;
        }

        if (!int.TryParse(fields[1], out var parentProcessId)
            || !long.TryParse(fields[11], out var userTicks)
            || !long.TryParse(fields[12], out var systemTicks))
        {
            return null;
        }

        return new ProcStatLine(name, parentProcessId, userTicks, systemTicks);
    }
}
