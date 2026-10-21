namespace Cockpit.Core.HeavyRuns;

// AC-1535: the heavy-run queue's ceilings, persisted under `heavyRuns` in `cockpit.json`. The defaults are measured:
// on 13 and 14-10 gates failed on database timeouts with the machine at 70-94% CPU.
public sealed record HeavyRunSettings
{
    public double CpuCeilingPercent { get; init; } = 75;

    public double MemoryCeilingPercent { get; init; } = 85;

    // A failure that looks like an overloaded machine lowers both ceilings by this step, never below the floor.
    public double CeilingStepPercent { get; init; } = 5;

    public double CeilingFloorPercent { get; init; } = 50;

    // A timeout only counts against the ceiling when the machine was this busy while it happened.
    public double OverloadedAbovePercent { get; init; } = 65;

    // An exploring run that has waited this long is queued as if it were a pre-merge gate.
    public int AgingMinutes { get; init; } = 15;

    public HeavyRunSettings Lowered() => this with
    {
        CpuCeilingPercent = Math.Max(CeilingFloorPercent, CpuCeilingPercent - CeilingStepPercent),
        MemoryCeilingPercent = Math.Max(CeilingFloorPercent, MemoryCeilingPercent - CeilingStepPercent),
    };
}
