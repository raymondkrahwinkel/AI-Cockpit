namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// Classifies what a settled step's outcome counts as (AC-347) — the strict rule the reliability streak is built on.
/// A step that ran out of attempts is a run restart even on its first attempt; a step reworked at least once (the CEO's
/// validation sent it back, human or agent) is a review finding; anything else — including a deliberately skipped step
/// or an operator-answered blockade — is no correction at all.
/// </summary>
internal static class AutopilotCorrection
{
    /// <summary>The correction, if any, a settled step's status and attempt count amount to.</summary>
    public static AutopilotCorrectionKind Classify(AutopilotStepStatus status, int attempts)
    {
        if (status == AutopilotStepStatus.Failed)
        {
            return AutopilotCorrectionKind.RunRestart;
        }

        if (attempts > 1)
        {
            return AutopilotCorrectionKind.ReviewFinding;
        }

        return AutopilotCorrectionKind.None;
    }
}
