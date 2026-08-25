namespace Cockpit.Plugin.Autopilot;

// Classifies what a settled step's outcome counts as (AC-347). Reworks (an actual validation verdict) count
// as review findings; attempts alone (crash, stall, refused isolation, profile mismatch) count as run restarts,
// since those never saw a judgment. A failed step is always a run restart, even with reworks behind it.
internal static class AutopilotCorrection
{
    // The correction, if any, a settled step's status, attempt count and rework count amount to.
    public static AutopilotCorrectionKind Classify(AutopilotStepStatus status, int attempts, int reworks)
    {
        if (status == AutopilotStepStatus.Failed)
        {
            return AutopilotCorrectionKind.RunRestart;
        }

        if (reworks > 0)
        {
            return AutopilotCorrectionKind.ReviewFinding;
        }

        if (attempts > 1)
        {
            return AutopilotCorrectionKind.RunRestart;
        }

        return AutopilotCorrectionKind.None;
    }
}
