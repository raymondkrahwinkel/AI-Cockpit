namespace Cockpit.Core.Diagnostics;

// AC-1096: what a session's processes hold right now. `AbandonedCount` is how many the parent chain can no longer
// reach from the session's own process — what a tree walk stops counting the moment they are reparented, here 3,9
// GB of unseen build servers. AC-1310: `SpawnedCount` drops the root, so a pane holding only its pty runs nothing.
public sealed record SessionProcesses(ResourceSample Usage, int Count, int SpawnedCount, int AbandonedCount)
{
    public static readonly SessionProcesses None = new(ResourceSample.None, 0, 0, 0);
}
