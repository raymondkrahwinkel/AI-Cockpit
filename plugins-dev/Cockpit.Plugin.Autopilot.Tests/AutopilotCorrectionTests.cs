namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The AC-347 classification table: what a settled step's status and attempt count amount to. A failed step is always
/// a run restart, even on its very first attempt; a step reworked at least once is a review finding; everything else —
/// including a deliberately skipped step and a blockade the operator answered — is no correction at all.
/// </summary>
public class AutopilotCorrectionTests
{
    [Fact]
    public void Classify_Passed_WithOneAttempt_IsNone()
    {
        var kind = AutopilotCorrection.Classify(AutopilotStepStatus.Passed, attempts: 1);

        Assert.Equal(AutopilotCorrectionKind.None, kind);
    }

    [Fact]
    public void Classify_Passed_WithTwoAttempts_IsReviewFinding()
    {
        var kind = AutopilotCorrection.Classify(AutopilotStepStatus.Passed, attempts: 2);

        Assert.Equal(AutopilotCorrectionKind.ReviewFinding, kind);
    }

    [Fact]
    public void Classify_Passed_WithThreeAttempts_IsReviewFinding()
    {
        var kind = AutopilotCorrection.Classify(AutopilotStepStatus.Passed, attempts: 3);

        Assert.Equal(AutopilotCorrectionKind.ReviewFinding, kind);
    }

    [Fact]
    public void Classify_Failed_WithOneAttempt_IsRunRestart()
    {
        var kind = AutopilotCorrection.Classify(AutopilotStepStatus.Failed, attempts: 1);

        Assert.Equal(AutopilotCorrectionKind.RunRestart, kind);
    }

    [Fact]
    public void Classify_Failed_WithSeveralAttempts_IsStillRunRestart()
    {
        var kind = AutopilotCorrection.Classify(AutopilotStepStatus.Failed, attempts: 3);

        Assert.Equal(AutopilotCorrectionKind.RunRestart, kind);
    }

    [Fact]
    public void Classify_Skipped_IsNone()
    {
        var kind = AutopilotCorrection.Classify(AutopilotStepStatus.Skipped, attempts: 1);

        Assert.Equal(AutopilotCorrectionKind.None, kind);
    }

    [Fact]
    public void Classify_Blocked_IsNone()
    {
        var kind = AutopilotCorrection.Classify(AutopilotStepStatus.Blocked, attempts: 1);

        Assert.Equal(AutopilotCorrectionKind.None, kind);
    }

    [Fact]
    public void Classify_Pending_IsNone()
    {
        var kind = AutopilotCorrection.Classify(AutopilotStepStatus.Pending, attempts: 0);

        Assert.Equal(AutopilotCorrectionKind.None, kind);
    }

    [Fact]
    public void Classify_Running_IsNone()
    {
        var kind = AutopilotCorrection.Classify(AutopilotStepStatus.Running, attempts: 1);

        Assert.Equal(AutopilotCorrectionKind.None, kind);
    }
}
