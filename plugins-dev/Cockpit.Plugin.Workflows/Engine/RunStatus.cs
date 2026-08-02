namespace Cockpit.Plugin.Workflows.Engine;

// How a run, or one step of it, ended (#69).
public enum RunStatus
{
    Running,

    Succeeded,

    Failed,

    // Passed by: the step was switched off, or this build has no way to execute its type — said out loud rather than counted as a success.
    Skipped,
}
