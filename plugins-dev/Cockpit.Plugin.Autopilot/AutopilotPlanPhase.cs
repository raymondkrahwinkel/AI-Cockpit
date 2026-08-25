namespace Cockpit.Plugin.Autopilot;

// Where a CEO-planned run sits (AC-174). `Planning` is the interactive round with the CEO; `Running` is the
// autonomous execution after the single approval; `AwaitingOperator`/`Blocked` cover the AC-155 blockade;
// `MergeReady` is the settled end (every hard step passed — the merge itself stays with the human).
internal enum AutopilotPlanPhase
{
    // The interactive planning round: the CEO drafts and revises the plan with the operator, no run yet.
    Planning,

    // Approved and self-driving: the steps run one by one on their profiles.
    Running,

    // A step hit a decision only the operator can make (AC-155) — waiting for their answer.
    AwaitingOperator,

    // Parked: a hard step did not pass, or a blockade went unanswered past its grace time.
    Blocked,

    // Every hard step passed — the PR is merge-ready and the merge is left to the human.
    MergeReady,

    // The operator stopped the run mid-flight (AC-196) — settled by their choice, not the step policy; any
    // unmerged work is left as-is. Kept last so its integer value is appended — persisted history keeps
    // deserializing MergeReady/Blocked unchanged.
    Stopped,
}
