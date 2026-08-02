namespace Cockpit.Plugin.Autopilot;

// Classifies what a settled step's outcome counts as (AC-347) — the strict rule the reliability streak is built on.
// The rework count is the event itself — a validation actually sent the step back — rather than a proxy inferred from
// attempts: `AutopilotStep.Attempts` also grows on a restart that never saw a verdict (a crashed session,
// a stall timeout, a refused isolation, a profile/model mismatch), which is a run restart, not a review finding.
// A failed step is always a run restart, even with reworks behind it — it never settled on a judgment, only gave up.
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
