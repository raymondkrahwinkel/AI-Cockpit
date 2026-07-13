namespace Cockpit.Core.Diagnostics;

/// <summary>
/// How much memory this machine has. Needed because "the cockpit is using four gigabytes" means nothing on its own —
/// it is a problem on a laptop with eight and unremarkable on a workstation with sixty-four.
/// <para>
/// .NET knows this already (<see cref="GC.GetGCMemoryInfo"/> reports the total available to the process), so this is a
/// small class rather than three platform readers: what it mostly is, is the honest zero it returns when the runtime
/// will not say. A share of an unknown total is not a fact, and nothing is warned about on a guess.
/// </para>
/// </summary>
public static class MachineMemory
{
    public static long TotalBytes()
    {
        var info = GC.GetGCMemoryInfo();

        return info.TotalAvailableMemoryBytes > 0 ? info.TotalAvailableMemoryBytes : 0;
    }
}
