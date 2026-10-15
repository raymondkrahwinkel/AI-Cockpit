namespace Cockpit.Plugin.Schedules.Contracts;

public enum RunOutcome
{
    Running,
    Succeeded,
    Failed,
    NothingToDeliver,
    Skipped,
    Missed,
}
