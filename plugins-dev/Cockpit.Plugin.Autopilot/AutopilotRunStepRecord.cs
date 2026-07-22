namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// A finished step in a run's history (Raymond 2026-07-22): its title and how it ended, plus the run's last note on it
/// (why it failed, or a closing line). Kept small and value-only so the record persists cleanly and reads back after a
/// restart — it is a snapshot of the outcome, not a live step.
/// </summary>
internal sealed record AutopilotRunStepRecord(string Title, AutopilotStepStatus Status, string Note);
