namespace Cockpit.Plugin.Autopilot;

// Where a step of a CEO-built plan currently sits (AC-174). Deliberately generic: a coding run's steps
// (code, verify, review, security, conventions, PR) and a future non-coding run's steps (AC-158 — e.g. an
// invoice round) share the same states, so the pipeline surface renders whatever plan the CEO builds.
internal enum AutopilotStepStatus
{
    // Not started yet — waiting its turn in the plan.
    Pending,

    // The active step: its session is running the work now.
    Running,

    // Finished and met its acceptance.
    Passed,

    // Finished but did not meet its acceptance — parks the run when the step is a hard gate.
    Failed,

    // Left out on purpose — a skippable step whose capability was absent, noted rather than run.
    Skipped,

    // Waiting on the operator (AC-155 blockade) before it can go on.
    Blocked,
}
