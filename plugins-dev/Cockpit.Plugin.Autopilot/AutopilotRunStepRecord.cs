namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// A finished step in a run's history: its title and how it ended, the run's last note on it (why it failed, or a
/// closing line), how many attempts it took, and the AC-347 correction it was classified as. Kept small and value-only
/// so the record persists cleanly and reads back after a restart — it is a snapshot of the outcome, not a live step.
/// The three new fields are init-properties, not positional parameters, so persisted history from before AC-347 still
/// deserializes — a missing field just reads back its default.
/// </summary>
internal sealed record AutopilotRunStepRecord(string Title, AutopilotStepStatus Status, string Note)
{
    /// <summary>How many times this step was started — the rework counter <see cref="AutopilotCorrection.Classify"/> reads.</summary>
    public int Attempts { get; init; }

    /// <summary>What this step's outcome counts as (AC-347) — classified automatically at settle, or set by the operator.</summary>
    public AutopilotCorrectionKind Correction { get; init; }

    /// <summary>Whether <see cref="Correction"/> came from the automatic classifier or an operator override.</summary>
    public AutopilotCorrectionSource CorrectionSource { get; init; }
}
