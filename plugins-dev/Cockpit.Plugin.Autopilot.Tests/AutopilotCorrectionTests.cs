namespace Cockpit.Plugin.Autopilot.Tests;

// The AC-347 classification table: a failed step is always a run restart; a step reworked at least once (validation
// sent it back) is a review finding; a step restarted without ever being reworked (crashed session, stall timeout,
// refused isolation, profile/model mismatch) is a run restart, not a review finding; everything else is no correction.
public class AutopilotCorrectionTests
{
    // The rows travel as object so this source's own signature stays public while the members are still named rather
    // than numbered; the test casts them back once. `[InlineData]` cannot carry them — a public test method may not
    // name an internal type in its signature (CS0051), and xUnit1000 forbids making the class internal instead.
    public static IEnumerable<object[]> Classifications() =>
    [
        [AutopilotStepStatus.Passed, 1, 0, AutopilotCorrectionKind.None],
        // Restarted (attempts > 1) but never reworked (no validation ever sent it back): a crashed session, a stall
        // timeout, a refused isolation, or a profile/model mismatch — a restart without a judgment behind it.
        [AutopilotStepStatus.Passed, 3, 0, AutopilotCorrectionKind.RunRestart],
        [AutopilotStepStatus.Passed, 3, 2, AutopilotCorrectionKind.ReviewFinding],
        [AutopilotStepStatus.Passed, 2, 1, AutopilotCorrectionKind.ReviewFinding],
        [AutopilotStepStatus.Failed, 1, 0, AutopilotCorrectionKind.RunRestart],
        [AutopilotStepStatus.Failed, 3, 0, AutopilotCorrectionKind.RunRestart],
        // The Failed branch wins even when the step was reworked along the way — it never settled on a judgment, it
        // gave up after its last attempt.
        [AutopilotStepStatus.Failed, 6, 5, AutopilotCorrectionKind.RunRestart],
        [AutopilotStepStatus.Skipped, 1, 0, AutopilotCorrectionKind.None],
        [AutopilotStepStatus.Blocked, 1, 0, AutopilotCorrectionKind.None],
        [AutopilotStepStatus.Pending, 0, 0, AutopilotCorrectionKind.None],
        [AutopilotStepStatus.Running, 1, 0, AutopilotCorrectionKind.None],
    ];

    [Theory]
    [MemberData(nameof(Classifications))]
    public void Classify_ReadsTheStatusAttemptsAndReworks(object status, int attempts, int reworks, object expected) =>
        Assert.Equal(
            (AutopilotCorrectionKind)expected,
            AutopilotCorrection.Classify((AutopilotStepStatus)status, attempts, reworks));
}
