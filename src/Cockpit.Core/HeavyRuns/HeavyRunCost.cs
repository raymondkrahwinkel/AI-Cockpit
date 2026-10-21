namespace Cockpit.Core.HeavyRuns;

// AC-1535: what one kind of run in one repository measurably added to the machine at its peak, learned from its
// earlier runs. Unknown until the first run of that kind finished.
public sealed record HeavyRunCost(double CpuPercent, long MemoryBytes, long DiskBytes);
