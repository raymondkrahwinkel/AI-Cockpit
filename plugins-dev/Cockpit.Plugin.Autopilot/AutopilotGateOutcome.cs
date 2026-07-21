namespace Cockpit.Plugin.Autopilot;

/// <summary>What the agent reported for a done-gate (AC-153): it passed, it failed, or it could not run (capability absent).</summary>
internal enum AutopilotGateOutcome
{
    Passed,
    Failed,
    Skipped,
}
