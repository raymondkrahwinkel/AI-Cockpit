namespace Cockpit.Core.Diagnostics;

// AC-1013 (was #78): What a process tree was using at one moment. CPU is accumulated processor `CpuTime`, not
// a percentage — a percentage only exists between two samples, and the caller taking two samples should be the
// one computing it. `WorkingSetBytes` is resident memory in use now, not reserved.
public sealed record ResourceSample(TimeSpan CpuTime, long WorkingSetBytes)
{
    public static readonly ResourceSample None = new(TimeSpan.Zero, 0);
}
