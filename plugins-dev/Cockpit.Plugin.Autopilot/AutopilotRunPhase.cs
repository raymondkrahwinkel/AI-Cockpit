namespace Cockpit.Plugin.Autopilot;

/// <summary>Where a run is in the flow (AC-151/AC-152/AC-153): being scoped, refused before it started, running its
/// session, blocked at a hard done-gate, or merge-ready (all required gates passed — the merge stays with the human).</summary>
internal enum AutopilotRunPhase
{
    Scoping,
    Refused,
    Running,
    Blocked,
    MergeReady,
}
