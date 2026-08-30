namespace Cockpit.Core.Diagnostics;

// AC-1096: what a session's processes hold right now. `AbandonedCount` is how many of them the parent chain can
// no longer reach from the session's own process — precisely the ones a tree walk stops counting the moment they
// are reparented, which on this machine was 3,9 GB of build servers nobody could see.
public sealed record SessionProcesses(ResourceSample Usage, int Count, int AbandonedCount)
{
    public static readonly SessionProcesses None = new(ResourceSample.None, 0, 0);
}
