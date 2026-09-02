namespace Cockpit.Plugin.Autopilot.Tests;

// `AutopilotRunRecord.Capture` — the write path from live plan state to the persisted record shape, extracted out
// of `_RecordAndNotify` so it is unit-testable. Previously untested: mutations like a fixed `None` or hard-coded
// `Operator`/blanked fields left every test green. Each fixture gives each field its own distinct value so a swap can't hide.
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
    public void Capture_ProjectsEachStepOnItsOwnValues_NeverOneStepsOntoTheRest()
    {
        // Three steps, no two alike in anything the record carries: order, title, tier, attempt count and what those
        // attempts classify as. That is the whole design of this fixture — a mutation that hard-codes any single
        // field (a fixed `None`, a blanked model, a zero attempt count, one step's tier for all) cannot satisfy
        // three different expectations at once, where a record of identical steps would let it pass.
        //
        // AC-256 is why the tier is here at all: live it shows as a chip on the block and then vanishes with the run,
        // so without it the tier mix of a finished run — what a before/after on model cost is measured from — cannot
        // be read back at all.
        var plan = new AutopilotPlan("goal", null,
        [
            Step("1", "Alpha", attempts: 4, reworks: 2) with { ProfileLabel = "Claude", Model = "haiku" },
            Step("2", "Beta", attempts: 7, reworks: 0) with { ProfileLabel = "Qwen (local)", Model = null },
            Step("3", "Gamma", attempts: 1, reworks: 0) with { ProfileLabel = "Claude", Model = "opus" },
        ]);

        var record = AutopilotRunRecord.Capture(plan, AutopilotPlanPhase.MergeReady, null, "run-1", 0, false, FinishedAt);

        Assert.Equal(["Alpha", "Beta", "Gamma"], record.Steps.Select(step => step.Title));

        Assert.Equal("Claude", record.Steps[0].ProfileLabel);
        Assert.Equal("haiku", record.Steps[0].Model);
        Assert.Equal("Qwen (local)", record.Steps[1].ProfileLabel);
        // A profile that pins its own model leaves this empty rather than null, so the record round-trips as JSON.
        Assert.Equal(string.Empty, record.Steps[1].Model);
        Assert.Equal("opus", record.Steps[2].Model);

        Assert.Equal([4, 7, 1], record.Steps.Select(step => step.Attempts));

        Assert.Equal(AutopilotCorrectionKind.ReviewFinding, record.Steps[0].Correction);
        Assert.Equal(AutopilotCorrectionKind.RunRestart, record.Steps[1].Correction);
        Assert.Equal(AutopilotCorrectionKind.None, record.Steps[2].Correction);

        // Everything Capture writes is the harness's own reading, never an operator reclassification.
        Assert.All(record.Steps, step => Assert.Equal(AutopilotCorrectionSource.Automatic, step.CorrectionSource));
    }

    [Fact]
    public void Capture_CarriesTheRunLevelFields_EachFromItsOwnArgument()
    {
        var plan = new AutopilotPlan("goal", new AutopilotPlanSource("youtrack", "AC-999", "t"), [Step("1", "Step", attempts: 1, reworks: 0)]);

        var record = AutopilotRunRecord.Capture(plan, AutopilotPlanPhase.Blocked, "a hard step failed", "run-distinct-id", 5, true, FinishedAt);

        Assert.Equal("run-distinct-id", record.RunId);
        Assert.Equal("AC-999", record.Ticket);
        Assert.Equal(5, record.BlockadeAnswers);
        Assert.True(record.PullRequestMissing);
        Assert.Equal(AutopilotPlanPhase.Blocked, record.Outcome);
        Assert.Equal("a hard step failed", record.BlockReason);
        // From the timestamp it was given, never from DateTimeOffset.Now.
        Assert.Equal(FinishedAt.ToString("o"), record.FinishedAt);
    }

    [Theory]
    // AC-346: the epic-progress comment filters history down to just this epic's own runs by this field — a
    // mutation that dropped it would leave every epic's progress comment blended with the whole run history.
    [InlineData("AC-EPIC", "AC-EPIC")]
    // A run the operator clicked directly belongs to no epic, and says so rather than borrowing one.
    [InlineData("", "")]
    public void Capture_CarriesTheSourcesEpicId(string epicId, string expected)
    {
        var plan = new AutopilotPlan(
            "goal",
            new AutopilotPlanSource("youtrack", "AC-2", "t") { EpicId = epicId },
            [Step("1", "Step", attempts: 1, reworks: 0)]);

        var record = AutopilotRunRecord.Capture(plan, AutopilotPlanPhase.MergeReady, null, "run-1", 0, false, FinishedAt);

        Assert.Equal(expected, record.EpicId);
    }

    [Theory]
    [InlineData("Claude", "haiku", "· Claude · haiku")]
    // A profile that pins its own model has no choice to show, so only the profile is named.
    [InlineData("Qwen (local)", "", "· Qwen (local)")]
    // History written before AC-256 has neither, and must render nothing rather than a stray separator.
    [InlineData("", "", "")]
    public void HistoryStepTier_ShowsTheModelOnlyWhenThereWasAChoice(string profileLabel, string model, string expected) =>
        Assert.Equal(expected, AutopilotPlanWorkspaceBody._HistoryStepTier(
            new AutopilotRunStepRecord("t", AutopilotStepStatus.Passed, string.Empty) { ProfileLabel = profileLabel, Model = model }));
}
