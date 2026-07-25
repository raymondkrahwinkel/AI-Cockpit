namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// Where a step of a CEO-built plan currently sits (AC-174). Deliberately generic: a coding run's steps
/// (code, verify, review, security, conventions, PR) and a future non-coding run's steps (AC-158 — e.g. an
/// invoice round) share the same states, so the pipeline surface renders whatever plan the CEO builds.
/// </summary>
internal enum AutopilotStepStatus
{
    /// <summary>Not started yet — waiting its turn in the plan.</summary>
    Pending,

    /// <summary>The active step: its session is running the work now.</summary>
    Running,

    /// <summary>Finished and met its acceptance.</summary>
    Passed,

    /// <summary>Finished but did not meet its acceptance — parks the run when the step is a hard gate.</summary>
    Failed,

    /// <summary>Left out on purpose — a skippable step whose capability was absent, noted rather than run.</summary>
    Skipped,

    /// <summary>Waiting on the operator (AC-155 blockade) before it can go on.</summary>
    Blocked,
}
