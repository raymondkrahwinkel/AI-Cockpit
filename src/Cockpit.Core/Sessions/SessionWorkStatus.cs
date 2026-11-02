using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.Core.Sessions;

public static class SessionWorkStatus
{
    public static bool HasOutstandingWork(bool hasBackgroundShells, IReadOnlyList<SessionRunActivity> runs) =>
        hasBackgroundShells || runs.Count > 0;

    public static SessionStatus Derive(SessionStatus status, IReadOnlyList<SessionRunActivity> runs) =>
        runs.Count == 0 && status is SessionStatus.Idle or SessionStatus.Done
            ? SessionStatus.WorkingBackground
            : status;

    public static SessionLiveState Derive(SessionLiveState state, IReadOnlyList<SessionRunActivity> runs) =>
        state with { Status = Derive(state.Status, runs), ActiveRuns = runs };
}
