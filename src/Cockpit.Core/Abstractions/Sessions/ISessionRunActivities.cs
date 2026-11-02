namespace Cockpit.Core.Abstractions.Sessions;

public interface ISessionRunActivities
{
    event Action? Changed;

    IReadOnlyList<SessionRunActivity> RunsForPane(string paneId);
}
