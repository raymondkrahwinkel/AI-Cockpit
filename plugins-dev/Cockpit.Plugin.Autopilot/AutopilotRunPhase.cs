namespace Cockpit.Plugin.Autopilot;

/// <summary>Where a run is in the flow (AC-151/AC-152/AC-153/AC-155): being scoped, refused before it started, running
/// its session, waiting on the operator to answer a blockade question, blocked at a hard done-gate, or merge-ready
/// (all required gates passed — the merge stays with the human).</summary>
internal enum AutopilotRunPhase
{
    Scoping,
    Refused,
    Running,
    AwaitingOperator,
    Blocked,
    MergeReady,
}
