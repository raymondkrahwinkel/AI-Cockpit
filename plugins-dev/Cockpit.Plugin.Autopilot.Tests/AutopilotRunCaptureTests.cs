namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// <see cref="AutopilotRunRecord.Capture"/> — the write path from live <see cref="AutopilotPlan"/> state to the
/// persisted <see cref="AutopilotRunRecord"/> shape, extracted out of <c>AutopilotPlanWorkspaceBody._RecordAndNotify</c>
/// so it is unit-testable without a UI. This is the one place these mappings were previously untested: a mutation
/// replacing the <see cref="AutopilotCorrection.Classify"/> call with a fixed <see cref="AutopilotCorrectionKind.None"/>,
/// dropping <see cref="AutopilotStep.Attempts"/> to 0, hard-coding <see cref="AutopilotCorrectionSource.Operator"/>, or
/// blanking <c>Ticket</c>/<c>RunId</c>/<c>BlockadeAnswers</c>/<c>PullRequestMissing</c> all left every existing test
/// green. Every fixture below gives each step and each field its own distinct, recognisable value, so a swapped or
/// hard-coded value cannot hide behind two fixtures happening to share one.
/// </summary>
public class AutopilotRunCaptureTests
{
    private static readonly DateTimeOffset FinishedAt = new(2026, 7, 28, 12, 30, 0, TimeSpan.Zero);

    private static AutopilotStep Step(string id, string title, int attempts, int reworks) =>
        new(id, title, "desc", "Claude", "opus", "brief", "acceptance", GateMode.Hard, AutopilotStepStatus.Passed)
        {
            Attempts = attempts,
            Reworks = reworks,
        };

    [Fact]
    public void Capture_KeepsTheTierEachStepActuallyRanOn()
    {
        // AC-256: live this shows as a chip on the block and then vanishes with the run. Without it the tier mix of a
        // finished run cannot be read back at all, which is what a before/after on model cost is measured from. Two
        // steps on different tiers, so a mutation carrying one step's tier to every step cannot pass.
        var plan = new AutopilotPlan("goal", null,
        [
            Step("1", "Cheap step", attempts: 1, reworks: 0) with { ProfileLabel = "Claude", Model = "haiku" },
            Step("2", "Local step", attempts: 1, reworks: 0) with { ProfileLabel = "Qwen (local)", Model = null },
        ]);

        var record = AutopilotRunRecord.Capture(plan, AutopilotPlanPhase.MergeReady, null, "run-1", 0, false, FinishedAt);

        Assert.Equal("Claude", record.Steps[0].ProfileLabel);
        Assert.Equal("haiku", record.Steps[0].Model);
        Assert.Equal("Qwen (local)", record.Steps[1].ProfileLabel);
        // A profile that pins its own model leaves this empty rather than null, so the record round-trips as JSON.
        Assert.Equal(string.Empty, record.Steps[1].Model);
    }

    [Fact]
    public void HistoryStepTier_ShowsTheModelOnlyWhenThereWasAChoice()
    {
        Assert.Equal("· Claude · haiku", AutopilotPlanWorkspaceBody._HistoryStepTier(
            new AutopilotRunStepRecord("t", AutopilotStepStatus.Passed, string.Empty) { ProfileLabel = "Claude", Model = "haiku" }));

        Assert.Equal("· Qwen (local)", AutopilotPlanWorkspaceBody._HistoryStepTier(
            new AutopilotRunStepRecord("t", AutopilotStepStatus.Passed, string.Empty) { ProfileLabel = "Qwen (local)" }));

        // History written before AC-256 has neither, and must render nothing rather than a stray separator.
        Assert.Equal(string.Empty, AutopilotPlanWorkspaceBody._HistoryStepTier(
            new AutopilotRunStepRecord("t", AutopilotStepStatus.Passed, string.Empty)));
    }

    [Fact]
    public void Capture_ClassifiesEachStepIndependently_NotAFixedValue()
    {
        // A reworked step and a clean step in the SAME record: a mutation that hard-codes Classify's result to None
        // (or any single constant) cannot pass both assertions at once, unlike a record where every step is identical.
        var plan = new AutopilotPlan("goal", null,
        [
            Step("1", "Reworked step", attempts: 3, reworks: 2),
            Step("2", "Clean step", attempts: 1, reworks: 0),
        ]);

        var record = AutopilotRunRecord.Capture(plan, AutopilotPlanPhase.MergeReady, null, "run-1", 0, false, FinishedAt);

        Assert.Equal(AutopilotCorrectionKind.ReviewFinding, record.Steps[0].Correction);
        Assert.Equal(AutopilotCorrectionKind.None, record.Steps[1].Correction);
    }

    [Fact]
    public void Capture_CarriesEachStepsOwnAttemptCount_NotAFixedZero()
    {
        var plan = new AutopilotPlan("goal", null,
        [
            Step("1", "First", attempts: 4, reworks: 0),
            Step("2", "Second", attempts: 7, reworks: 0),
        ]);

        var record = AutopilotRunRecord.Capture(plan, AutopilotPlanPhase.MergeReady, null, "run-1", 0, false, FinishedAt);

        Assert.Equal(4, record.Steps[0].Attempts);
        Assert.Equal(7, record.Steps[1].Attempts);
    }

    [Fact]
    public void Capture_ClassifiesTheCorrectionAsAutomatic_NotOperator()
    {
        var plan = new AutopilotPlan("goal", null, [Step("1", "Step", attempts: 1, reworks: 0)]);

        var record = AutopilotRunRecord.Capture(plan, AutopilotPlanPhase.MergeReady, null, "run-1", 0, false, FinishedAt);

        Assert.Equal(AutopilotCorrectionSource.Automatic, record.Steps[0].CorrectionSource);
    }

    [Fact]
    public void Capture_CarriesTicket_RunId_BlockadeAnswers_AndPullRequestMissing_AsDistinctValues()
    {
        var plan = new AutopilotPlan("goal", new AutopilotPlanSource("youtrack", "AC-999", "t"), [Step("1", "Step", attempts: 1, reworks: 0)]);

        var record = AutopilotRunRecord.Capture(plan, AutopilotPlanPhase.MergeReady, null, "run-distinct-id", 5, true, FinishedAt);

        Assert.Equal("run-distinct-id", record.RunId);
        Assert.Equal("AC-999", record.Ticket);
        Assert.Equal(5, record.BlockadeAnswers);
        Assert.True(record.PullRequestMissing);
    }

    [Fact]
    public void Capture_KeepsStepOrderAndTitles()
    {
        var plan = new AutopilotPlan("goal", null,
        [
            Step("1", "Alpha", attempts: 1, reworks: 0),
            Step("2", "Beta", attempts: 1, reworks: 0),
            Step("3", "Gamma", attempts: 1, reworks: 0),
        ]);

        var record = AutopilotRunRecord.Capture(plan, AutopilotPlanPhase.MergeReady, null, "run-1", 0, false, FinishedAt);

        Assert.Equal(3, record.Steps.Count);
        Assert.Equal("Alpha", record.Steps[0].Title);
        Assert.Equal("Beta", record.Steps[1].Title);
        Assert.Equal("Gamma", record.Steps[2].Title);
    }

    [Fact]
    public void Capture_CarriesOutcomeAndBlockReason()
    {
        var plan = new AutopilotPlan("goal", null, [Step("1", "Step", attempts: 1, reworks: 0)]);

        var record = AutopilotRunRecord.Capture(plan, AutopilotPlanPhase.Blocked, "a hard step failed", "run-1", 0, false, FinishedAt);

        Assert.Equal(AutopilotPlanPhase.Blocked, record.Outcome);
        Assert.Equal("a hard step failed", record.BlockReason);
    }

    [Fact]
    public void Capture_FormatsFinishedAt_FromTheGivenTimestamp_NotDateTimeOffsetNow()
    {
        var plan = new AutopilotPlan("goal", null, [Step("1", "Step", attempts: 1, reworks: 0)]);

        var record = AutopilotRunRecord.Capture(plan, AutopilotPlanPhase.MergeReady, null, "run-1", 0, false, FinishedAt);

        Assert.Equal(FinishedAt.ToString("o"), record.FinishedAt);
    }
}
