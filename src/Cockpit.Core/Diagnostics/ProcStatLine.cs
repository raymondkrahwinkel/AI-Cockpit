namespace Cockpit.Core.Diagnostics;

/// <summary>
/// One line of Linux's <c>/proc/&lt;pid&gt;/stat</c>, as far as a resource meter cares (#78): who the parent is,
/// and how much processor time the process has burned. Pure, because parsing this file has one nasty trap and a
/// test is the only honest way to prove we did not fall into it — field 2 is the executable name <em>in
/// parentheses</em>, and it may itself contain spaces and parentheses (a process called "my prog (v2)" is legal),
/// so counting fields from the left is wrong. The reliable trick is to start counting after the LAST ')'.
/// </summary>
public sealed record ProcStatLine(int ParentProcessId, long UserTicks, long SystemTicks)
{
    public long TotalTicks => UserTicks + SystemTicks;

    public static ProcStatLine? Parse(string line)
    {
        var lastParenthesis = line.LastIndexOf(')');
        if (lastParenthesis < 0 || lastParenthesis + 2 >= line.Length)
        {
            return null;
        }

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

        return new ProcStatLine(parentProcessId, userTicks, systemTicks);
    }
}
