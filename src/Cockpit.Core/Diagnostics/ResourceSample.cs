namespace Cockpit.Core.Diagnostics;

// What a process (and everything it spawned) was using at one moment (#78). CPU is reported as accumulated
// processor *time*, not as a percentage: a percentage only exists between two samples, and the thing that
// takes two samples should be the one that computes it.
//
// `CpuTime`: Processor time consumed since the process started, across all its threads and children.
// `WorkingSetBytes`: Resident memory — what it actually occupies now, not what it reserved.
public sealed record ResourceSample(TimeSpan CpuTime, long WorkingSetBytes)
{
    public static readonly ResourceSample None = new(TimeSpan.Zero, 0);
}
