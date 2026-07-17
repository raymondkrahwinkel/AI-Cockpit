using System.Runtime;

namespace Cockpit.Core.Diagnostics;

/// <summary>
/// The managed side of the cockpit's memory (AC-58): the GC mode, the heap it holds, and how hard it has been
/// working. Once the Metal render leak was ruled out for AC-57, the leading suspect became un-disposed
/// subscriptions and timers — a managed leak — so a heap that climbs across refreshes, and gen2 counts that keep
/// ticking, are exactly what this section exists to make visible.
/// </summary>
/// <param name="IsServerGc">Server GC (multi-heap) vs Workstation. A non-web .NET app is Workstation; the panel says which, because it changes what "normal" memory looks like.</param>
/// <param name="HeapSizeBytes">The managed heap's current size, from <see cref="GCMemoryInfo"/>.</param>
/// <param name="TotalAllocatedBytes">Everything allocated since start, collected or not — a rate, watched across refreshes, tells allocation churn.</param>
/// <param name="LiveManagedBytes">What <see cref="GC.GetTotalMemory(bool)"/> believes is currently live (no collection forced).</param>
/// <param name="Gen0Collections">Gen0 collection count since start.</param>
/// <param name="Gen1Collections">Gen1 collection count since start.</param>
/// <param name="Gen2Collections">Gen2 collection count since start — the expensive ones; a steady climb at idle is a leak tell.</param>
public sealed record ManagedHeapInfo(
    bool IsServerGc,
    long HeapSizeBytes,
    long TotalAllocatedBytes,
    long LiveManagedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections)
{
    public static ManagedHeapInfo Current() => new(
        GCSettings.IsServerGC,
        GC.GetGCMemoryInfo().HeapSizeBytes,
        GC.GetTotalAllocatedBytes(),
        GC.GetTotalMemory(forceFullCollection: false),
        GC.CollectionCount(0),
        GC.CollectionCount(1),
        GC.CollectionCount(2));
}
