using System.Globalization;

namespace Cockpit.Core.HeavyRuns;

// AC-1535: two readings of the machine's cumulative CPU counters, as Linux's /proc/stat and Windows' GetSystemTimes
// hand them out. Only the difference between two readings means anything: the busy share of the time in between.
public readonly record struct CpuTimes(ulong Idle, ulong Total)
{
    // The aggregate `cpu` line: user nice system idle iowait irq softirq steal [guest guest_nice]. Guest time is
    // already counted in user, so only the first eight add up to the total; waiting on I/O is idle time.
    public static CpuTimes? FromProcStat(string procStat)
    {
        var line = procStat.Split('\n', 2)[0];
        if (!line.StartsWith("cpu ", StringComparison.Ordinal))
        {
            return null;
        }

        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8)
            .Select(field => ulong.Parse(field, CultureInfo.InvariantCulture)).ToArray();
        if (fields.Length < 5)
        {
            return null;
        }

        return new CpuTimes(fields[3] + fields[4], fields.Aggregate(0UL, (sum, field) => sum + field));
    }

    // Windows counts idle time inside kernel time, so kernel plus user is already the whole.
    public static CpuTimes FromSystemTimes(ulong idle, ulong kernel, ulong user) => new(idle, kernel + user);

    public static double BusyPercent(CpuTimes before, CpuTimes after)
    {
        if (after.Total <= before.Total)
        {
            return 0;
        }

        var total = (double)(after.Total - before.Total);
        var idle = after.Idle >= before.Idle ? after.Idle - before.Idle : 0;
        return Math.Clamp(100.0 * (1 - (idle / total)), 0, 100);
    }
}
