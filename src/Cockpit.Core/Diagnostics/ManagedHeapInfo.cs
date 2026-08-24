using System.Runtime;

namespace Cockpit.Core.Diagnostics;

// AC-1013 (AC-58): Managed side of memory — GC mode, heap size, allocation and collection counts — added
// because un-disposed subscriptions/timers were the leading leak suspect once the Metal render leak was ruled
// out for AC-57; a heap climbing across refreshes and ticking gen2 counts are exactly what this makes visible.
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
