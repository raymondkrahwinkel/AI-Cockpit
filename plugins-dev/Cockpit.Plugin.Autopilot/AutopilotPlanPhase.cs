namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// Where a CEO-planned run sits (AC-174). <see cref="Planning"/> is the interactive round with the CEO — the plan is a
/// living artifact being shaped; <see cref="Running"/> is the autonomous execution after the single approval;
/// <see cref="AwaitingOperator"/> and <see cref="Blocked"/> cover the AC-155 blockade; <see cref="MergeReady"/> is the
/// settled end (every hard step passed — the merge itself stays with the human).
/// </summary>
internal enum AutopilotPlanPhase
{
    /// <summary>The interactive planning round: the CEO drafts and revises the plan with the operator, no run yet.</summary>
    Planning,

    /// <summary>Approved and self-driving: the steps run one by one on their profiles.</summary>
    Running,

    /// <summary>A step hit a decision only the operator can make (AC-155) — waiting for their answer.</summary>
    AwaitingOperator,

    /// <summary>Parked: a hard step did not pass, or a blockade went unanswered past its grace time.</summary>
    Blocked,

    /// <summary>Every hard step passed — the PR is merge-ready and the merge is left to the human.</summary>
    MergeReady,
}
