namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The AC-347 classification table: what a settled step's status, attempt count and rework count amount to. A failed
/// step is always a run restart, whatever its rework count; a step reworked at least once — a validation actually sent
/// it back — is a review finding; a step restarted without ever being reworked (attempts &gt; 1 with zero reworks: a
/// crashed session, a stall timeout, a refused isolation, a profile/model mismatch) is a run restart, not a review
/// finding; everything else — including a deliberately skipped step and a blockade the operator answered — is no
/// correction at all.
/// </summary>
public class AutopilotCorrectionTests
{
    [Fact]
    public void Classify_Passed_WithOneAttempt_NoReworks_IsNone()
    {
        var kind = AutopilotCorrection.Classify(AutopilotStepStatus.Passed, attempts: 1, reworks: 0);

        Assert.Equal(AutopilotCorrectionKind.None, kind);
    }

    [Fact]
    public void Classify_Passed_WithThreeAttempts_NoReworks_IsRunRestart()
    {
        // Restarted (attempts > 1) but never reworked (no validation ever sent it back): a crashed session, a stall
        // timeout, a refused isolation, or a profile/model mismatch — a restart without a judgment behind it.
        var kind = AutopilotCorrection.Classify(AutopilotStepStatus.Passed, attempts: 3, reworks: 0);

        Assert.Equal(AutopilotCorrectionKind.RunRestart, kind);
    }

    [Fact]
    public void Classify_Passed_WithThreeAttempts_TwoReworks_IsReviewFinding()
    {
        var kind = AutopilotCorrection.Classify(AutopilotStepStatus.Passed, attempts: 3, reworks: 2);

        Assert.Equal(AutopilotCorrectionKind.ReviewFinding, kind);
    }

    [Fact]
    public void Classify_Passed_WithOneRework_IsReviewFinding()
    {
        var kind = AutopilotCorrection.Classify(AutopilotStepStatus.Passed, attempts: 2, reworks: 1);

        Assert.Equal(AutopilotCorrectionKind.ReviewFinding, kind);
    }

    [Fact]
    public void Classify_Failed_WithOneAttempt_NoReworks_IsRunRestart()
    {
        var kind = AutopilotCorrection.Classify(AutopilotStepStatus.Failed, attempts: 1, reworks: 0);

        Assert.Equal(AutopilotCorrectionKind.RunRestart, kind);
    }

    [Fact]
    public void Classify_Failed_WithSeveralAttempts_IsStillRunRestart()
    {
        var kind = AutopilotCorrection.Classify(AutopilotStepStatus.Failed, attempts: 3, reworks: 0);

        Assert.Equal(AutopilotCorrectionKind.RunRestart, kind);
    }

    [Fact]
    public void Classify_Failed_WithReworksBehindIt_IsStillRunRestart()
    {
        // The Failed branch wins even when the step was reworked along the way — it never settled on a judgment, it
        // gave up after its last attempt.
        var kind = AutopilotCorrection.Classify(AutopilotStepStatus.Failed, attempts: 6, reworks: 5);

        Assert.Equal(AutopilotCorrectionKind.RunRestart, kind);
    }

    [Fact]
    public void Classify_Skipped_IsNone()
    {
        var kind = AutopilotCorrection.Classify(AutopilotStepStatus.Skipped, attempts: 1, reworks: 0);

        Assert.Equal(AutopilotCorrectionKind.None, kind);
    }

    [Fact]
    public void Classify_Blocked_IsNone()
    {
        var kind = AutopilotCorrection.Classify(AutopilotStepStatus.Blocked, attempts: 1, reworks: 0);

        Assert.Equal(AutopilotCorrectionKind.None, kind);
    }

    [Fact]
    public void Classify_Pending_IsNone()
    {
        var kind = AutopilotCorrection.Classify(AutopilotStepStatus.Pending, attempts: 0, reworks: 0);

        Assert.Equal(AutopilotCorrectionKind.None, kind);
    }

    [Fact]
    public void Classify_Running_IsNone()
    {
        var kind = AutopilotCorrection.Classify(AutopilotStepStatus.Running, attempts: 1, reworks: 0);

        Assert.Equal(AutopilotCorrectionKind.None, kind);
    }
}
