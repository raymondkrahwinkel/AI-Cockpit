namespace Cockpit.Core.Sessions;

// AC-2026: a profile's parallel-session limit (0 = unlimited) and what runs under it now, split by route.
public sealed record ProfileSessionsView(int MaxParallelSessions, int OpenPanes, int DelegatedTasks)
{
    public int RunningSessions => OpenPanes + DelegatedTasks;
}
