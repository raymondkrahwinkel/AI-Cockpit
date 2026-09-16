namespace Cockpit.Core.Diagnostics;

// AC-1096: what a session's processes hold right now. `AbandonedCount` is how many the parent chain can no longer
// reach from the session's own process — reparented build servers a tree walk stops counting. AC-1310: `SpawnedCount`
// drops the root. AC-1331: `OutstandingCount` is how many still run under a shell the session started and waits on.
public sealed record SessionProcesses(ResourceSample Usage, int Count, int SpawnedCount, int AbandonedCount, int OutstandingCount)
{
    public static readonly SessionProcesses None = new(ResourceSample.None, 0, 0, 0, 0);
}
