namespace Cockpit.Core.HeavyRuns;

// AC-1535: the whole machine's load as the heavy-run queue judges it — every process counted, the cockpit's own
// runs, a dev build and a container alike. Smoothed over the sample window, so one spike does not decide.
public sealed record MachineLoad(double CpuPercent, long AvailableMemoryBytes, long TotalMemoryBytes)
{
    public static readonly MachineLoad Unknown = new(0, 0, 0);

    public long UsedMemoryBytes => Math.Max(0, TotalMemoryBytes - AvailableMemoryBytes);

    public double MemoryPercent => TotalMemoryBytes > 0 ? 100.0 * UsedMemoryBytes / TotalMemoryBytes : 0;
}
