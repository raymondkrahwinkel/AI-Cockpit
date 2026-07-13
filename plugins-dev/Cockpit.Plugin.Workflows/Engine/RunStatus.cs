namespace Cockpit.Plugin.Workflows.Engine;

/// <summary>How a run, or one step of it, ended (#69).</summary>
public enum RunStatus
{
    Running,

    Succeeded,

    Failed,

    /// <summary>Passed by: the step was switched off, or this build has no way to execute its type — said out loud rather than counted as a success.</summary>
    Skipped,
}
