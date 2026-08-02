using System.Runtime;

namespace Cockpit.Core.Diagnostics;

// The managed side of the cockpit's memory (AC-58): the GC mode, the heap it holds, and how hard it has been
// working. Once the Metal render leak was ruled out for AC-57, the leading suspect became un-disposed
// subscriptions and timers — a managed leak — so a heap that climbs across refreshes, and gen2 counts that keep
// ticking, are exactly what this section exists to make visible.
//
// `IsServerGc`: Server GC (multi-heap) vs Workstation. A non-web .NET app is Workstation; the panel says which, because it changes what "normal" memory looks like.
// `HeapSizeBytes`: The managed heap's current size, from `GCMemoryInfo`.
// `TotalAllocatedBytes`: Everything allocated since start, collected or not — a rate, watched across refreshes, tells allocation churn.
// `LiveManagedBytes`: What `GC.GetTotalMemory(bool)` believes is currently live (no collection forced).
// `Gen0Collections`: Gen0 collection count since start.
// `Gen1Collections`: Gen1 collection count since start.
// `Gen2Collections`: Gen2 collection count since start — the expensive ones; a steady climb at idle is a leak tell.
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
