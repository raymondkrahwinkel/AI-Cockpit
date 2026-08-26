namespace Cockpit.Core.Diagnostics;

// AC-1060: one `systemd-oomd` kill line. It names the cgroup Cockpit created for the session itself, which is
// the point — oomd kills the group with `cgroup.kill`, leaving no exit code and no `oom_kill` count to read.
public sealed record OomdKillLine(string CgroupName, string Pressure)
{
    private const string Prefix = "Killed ";

    private const string Reason = " due to memory pressure for ";

    // Null for any other line oomd logs. The caller matches `CgroupName` against the group it created; matching
    // on the message text instead would also hit Cockpit's own log line, which names the same group.
    public static OomdKillLine? Parse(string line)
    {
        var killed = line.IndexOf(Prefix + '/', StringComparison.Ordinal);
        var reason = line.IndexOf(Reason, StringComparison.Ordinal);
        if (killed < 0 || reason <= killed)
        {
            return null;
        }

        var path = line[(killed + Prefix.Length)..reason].TrimEnd();
        var name = path[(path.LastIndexOf('/') + 1)..];
        if (name.Length == 0)
        {
            return null;
        }

        // Everything after "being" is oomd's own account of why: the share, the limit, and the window it held
        // for. Kept verbatim rather than re-worded, so the message and the journal say the same thing.
        var being = line.IndexOf(" being ", reason, StringComparison.Ordinal);
        var pressure = being < 0 ? string.Empty : line[(being + " being ".Length)..].Trim();

        return new OomdKillLine(name, pressure);
    }
}
