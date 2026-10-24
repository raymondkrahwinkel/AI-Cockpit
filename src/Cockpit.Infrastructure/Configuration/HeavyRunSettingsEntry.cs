using Cockpit.Core.HeavyRuns;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of `HeavyRunSettings` in the `heavyRuns` section of `cockpit.json` (AC-1535). A field left out of a
// hand-edited section keeps its measured default rather than reading as 0, which would admit nothing.
internal sealed class HeavyRunSettingsEntry
{
    private static readonly HeavyRunSettings Defaults = new();

    public double CpuCeilingPercent { get; set; } = Defaults.CpuCeilingPercent;
    public double MemoryCeilingPercent { get; set; } = Defaults.MemoryCeilingPercent;
    public double CeilingStepPercent { get; set; } = Defaults.CeilingStepPercent;
    public double CeilingFloorPercent { get; set; } = Defaults.CeilingFloorPercent;
    public double OverloadedAbovePercent { get; set; } = Defaults.OverloadedAbovePercent;
    public int AgingMinutes { get; set; } = Defaults.AgingMinutes;

    public static HeavyRunSettingsEntry FromDomain(HeavyRunSettings settings) => new()
    {
        CpuCeilingPercent = settings.CpuCeilingPercent,
        MemoryCeilingPercent = settings.MemoryCeilingPercent,
        CeilingStepPercent = settings.CeilingStepPercent,
        CeilingFloorPercent = settings.CeilingFloorPercent,
        OverloadedAbovePercent = settings.OverloadedAbovePercent,
        AgingMinutes = settings.AgingMinutes,
    };

    public HeavyRunSettings ToDomain() => new()
    {
        CpuCeilingPercent = CpuCeilingPercent,
        MemoryCeilingPercent = MemoryCeilingPercent,
        CeilingStepPercent = CeilingStepPercent,
        CeilingFloorPercent = CeilingFloorPercent,
        OverloadedAbovePercent = OverloadedAbovePercent,
        AgingMinutes = AgingMinutes,
    };
}
