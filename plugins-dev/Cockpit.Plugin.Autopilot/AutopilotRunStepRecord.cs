namespace Cockpit.Plugin.Autopilot;

// A finished step in a run's history: its title and how it ended, the run's last note on it, how many attempts and
// reworks it took, and the AC-347 correction it was classified as — a snapshot, not a live step. New fields are
// init-properties, not positional parameters, so persisted history from before AC-347 still deserializes.
internal sealed record AutopilotRunStepRecord(string Title, AutopilotStepStatus Status, string Note)
{
    // How many times this step was started — a restart counter, not a judgment; see `Reworks`.
    public int Attempts { get; init; }

    // How many times a validation sent this step back to rework — the judgment counter `AutopilotCorrection.Classify` reads.
    public int Reworks { get; init; }

    // What this step's outcome counts as (AC-347) — classified automatically at settle, or set by the operator.
    public AutopilotCorrectionKind Correction { get; init; }

    // Whether `Correction` came from the automatic classifier or an operator override.
    public AutopilotCorrectionSource CorrectionSource { get; init; }

    // The profile this step actually ran on (AC-256). Live it shows as a chip on the pipeline block, but nothing kept
    // it once the run settled, so the tier mix of a finished run could not be read back — which is what a before/after
    // on model cost needs. Empty for history written before this existed.
    public string ProfileLabel { get; init; } = string.Empty;

    // The model this step ran on, or empty for a profile that pins its own and for pre-AC-256 history.
    public string Model { get; init; } = string.Empty;
}
