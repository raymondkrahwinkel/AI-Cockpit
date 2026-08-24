namespace Cockpit.Core.Diagnostics;

// AC-1013: Machine total memory, needed since "4 GB used" means nothing without context (a problem on 8 GB,
// unremarkable on 64 GB). Uses `GC.GetGCMemoryInfo` rather than platform readers; returns 0 honestly when the
// runtime won't say, since a share of an unknown total is not a fact worth warning on.
public static class MachineMemory
{
    public static long TotalBytes()
    {
        var info = GC.GetGCMemoryInfo();

        return info.TotalAvailableMemoryBytes > 0 ? info.TotalAvailableMemoryBytes : 0;
    }
}
