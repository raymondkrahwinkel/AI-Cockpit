namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// A finished step in a run's history: its title and how it ended, the run's last note on it (why it failed, or a
/// closing line), how many attempts and reworks it took, and the AC-347 correction it was classified as. Kept small
/// and value-only so the record persists cleanly and reads back after a restart — it is a snapshot of the outcome, not
/// a live step. The new fields are init-properties, not positional parameters, so persisted history from before AC-347
/// still deserializes — a missing field just reads back its default.
/// </summary>
internal sealed record AutopilotRunStepRecord(string Title, AutopilotStepStatus Status, string Note)
{
    /// <summary>How many times this step was started — a restart counter, not a judgment; see <see cref="Reworks"/>.</summary>
    public int Attempts { get; init; }

    /// <summary>How many times a validation sent this step back to rework — the judgment counter <see cref="AutopilotCorrection.Classify"/> reads.</summary>
    public int Reworks { get; init; }

    /// <summary>What this step's outcome counts as (AC-347) — classified automatically at settle, or set by the operator.</summary>
    public AutopilotCorrectionKind Correction { get; init; }

    /// <summary>Whether <see cref="Correction"/> came from the automatic classifier or an operator override.</summary>
    public AutopilotCorrectionSource CorrectionSource { get; init; }

    /// <summary>
    /// The profile this step actually ran on (AC-256). Live it shows as a chip on the pipeline block, but nothing kept
    /// it once the run settled, so the tier mix of a finished run could not be read back — which is what a before/after
    /// on model cost needs. Empty for history written before this existed.
    /// </summary>
    public string ProfileLabel { get; init; } = string.Empty;

    /// <summary>The model this step ran on, or empty for a profile that pins its own and for pre-AC-256 history.</summary>
    public string Model { get; init; } = string.Empty;
}
