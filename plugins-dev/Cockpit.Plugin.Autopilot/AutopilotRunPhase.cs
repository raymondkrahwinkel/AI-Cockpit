namespace Cockpit.Plugin.Autopilot;

/// <summary>Where a run is in the opstart flow (AC-151): being scoped, refused before it started, or running its session.</summary>
internal enum AutopilotRunPhase
{
    Scoping,
    Refused,
    Running,
}
