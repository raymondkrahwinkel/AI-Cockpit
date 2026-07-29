namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The AC-174 plan controller: the planning round (living plan + single approval), the run driving its steps, and the
/// settle that reads the per-step hard/skip policy — the plan-based counterpart of AutopilotRunControllerTests.
/// </summary>
public class AutopilotPlanControllerTests
{
    private static AutopilotStep Step(string id, GateMode mode = GateMode.Skip, AutopilotStepStatus status = AutopilotStepStatus.Pending) =>
        new(id, $"Step {id}", "desc", "Claude", "Sonnet", "brief", "acceptance", mode, status);

    private static AutopilotPlan PlanWith(params AutopilotStep[] steps) =>
        new("Goal", null, steps);

    [Fact]
    public void BeginPlanning_SetsPlanningPhase_AndHoldsThePlan()
    {
        var controller = new AutopilotPlanController();
        var plan = PlanWith(Step("1"));

        controller.BeginPlanning(plan);

        Assert.Equal(AutopilotPlanPhase.Planning, controller.Phase);
        Assert.Same(plan, controller.Plan);
    }

    [Fact]
    public void UpdatePlan_ReplacesTheLivingPlan_DuringPlanning()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));

        var revised = PlanWith(Step("1"), Step("2"));
        controller.UpdatePlan(revised);

        Assert.Same(revised, controller.Plan);
        Assert.Equal(AutopilotPlanPhase.Planning, controller.Phase);
    }

    [Fact]
    public void Approve_AnEmptyPlan_IsRefused_AndStaysInPlanning()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(AutopilotPlan.Empty(null, "Goal"));

        Assert.False(controller.Approve());
        Assert.Equal(AutopilotPlanPhase.Planning, controller.Phase);
    }

    [Fact]
    public void Approve_WithSteps_FreezesThePlan_AndStartsRunning()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));

        Assert.True(controller.Approve());
        Assert.Equal(AutopilotPlanPhase.Running, controller.Phase);
    }

    [Fact]
    public void BeginPlanning_WhileARunIsLive_IsRefused_LeavingTheRunUntouched()
    {
        var controller = new AutopilotPlanController();
        Assert.True(controller.BeginPlanning(PlanWith(Step("1"))));
        controller.Approve();

        Assert.False(controller.BeginPlanning(PlanWith(Step("other"))));
        Assert.Equal(AutopilotPlanPhase.Running, controller.Phase);
        Assert.Equal("1", Assert.Single(controller.Plan!.Steps).Id);
    }

    [Fact]
    public void BeginPlanning_AfterASettledRun_StartsFresh()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));
        controller.Approve();
        controller.SettleStep("1", AutopilotStepStatus.Passed);
        controller.Settle();

        Assert.True(controller.BeginPlanning(PlanWith(Step("2"))));
        Assert.Equal(AutopilotPlanPhase.Planning, controller.Phase);
    }

    [Fact]
    public void StartStep_MarksItRunning_AndExposesItAsActive()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1"), Step("2")));
        controller.Approve();

        controller.StartStep("1");

        Assert.Equal("1", controller.ActiveStep!.Id);
        Assert.Equal(AutopilotStepStatus.Running, controller.ActiveStep!.Status);
    }

    [Fact]
    public void Settle_WhenEveryHardStepPassed_IsMergeReady()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1", GateMode.Hard), Step("2")));
        controller.Approve();
        controller.SettleStep("1", AutopilotStepStatus.Passed);
        controller.SettleStep("2", AutopilotStepStatus.Passed);

        Assert.True(controller.AllSettled);
        controller.Settle();

        Assert.Equal(AutopilotPlanPhase.MergeReady, controller.Phase);
        Assert.Null(controller.BlockReason);
    }

    [Fact]
    public void Settle_WhenAHardStepDidNotPass_IsBlocked_NamingIt()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1", GateMode.Hard), Step("2")));
        controller.Approve();
        controller.SettleStep("1", AutopilotStepStatus.Failed);
        controller.SettleStep("2", AutopilotStepStatus.Passed);

        controller.Settle();

        Assert.Equal(AutopilotPlanPhase.Blocked, controller.Phase);
        Assert.Contains("Step 1", controller.BlockReason);
    }

    [Fact]
    public void Settle_WhenOnlyASkippableStepFailed_IsStillMergeReady()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1", GateMode.Hard), Step("2", GateMode.Skip)));
        controller.Approve();
        controller.SettleStep("1", AutopilotStepStatus.Passed);
        controller.SettleStep("2", AutopilotStepStatus.Failed);

        controller.Settle();

        Assert.Equal(AutopilotPlanPhase.MergeReady, controller.Phase);
    }

    [Fact]
    public void StartStep_RecordsAnAttempt_EachTimeItRuns()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));
        controller.Approve();

        controller.StartStep("1");
        Assert.Equal(1, controller.Plan!.Steps[0].Attempts);

        controller.StartStep("1");
        Assert.Equal(2, controller.Plan!.Steps[0].Attempts);
    }

    [Fact]
    public void ValidateStep_WhenPassed_SettlesTheStep_AndDoesNotRework()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));
        controller.Approve();
        controller.StartStep("1");

        Assert.False(controller.ValidateStep("1", AutopilotStepOutcome.Passed, maxAttempts: 2));
        Assert.Equal(AutopilotStepStatus.Passed, controller.Plan!.Steps[0].Status);
        Assert.Equal(0, controller.Plan!.Steps[0].Reworks);
    }

    [Fact]
    public void ValidateStep_Rejected_WithAttemptsLeft_SendsItBackToRework_AndCountsTheRework()
    {
        // Rejected is a genuine CEO verdict — the step's output was judged against its acceptance and turned down —
        // so it is the one outcome that counts as a rework (AC-347).
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));
        controller.Approve();
        controller.StartStep("1"); // attempt 1

        Assert.True(controller.ValidateStep("1", AutopilotStepOutcome.Rejected, maxAttempts: 2));
        Assert.Equal(AutopilotStepStatus.Pending, controller.Plan!.Steps[0].Status);
        Assert.Equal(1, controller.Plan!.Steps[0].Reworks);
    }

    [Fact]
    public void ValidateStep_Rejected_WhenAttemptsAreExhausted_SettlesItFailed_BoundingTheLoop_AndDoesNotCountARework()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));
        controller.Approve();

        // Attempt 1 → rejected → rework; attempt 2 → rejected → out of attempts → Failed, no more rework.
        controller.StartStep("1");
        Assert.True(controller.ValidateStep("1", AutopilotStepOutcome.Rejected, maxAttempts: 2));
        controller.StartStep("1");
        Assert.False(controller.ValidateStep("1", AutopilotStepOutcome.Rejected, maxAttempts: 2));

        Assert.Equal(AutopilotStepStatus.Failed, controller.Plan!.Steps[0].Status);
        Assert.Equal(2, controller.Plan!.Steps[0].Attempts);
        // Only the first rejection sent it back to rework; the second ran out of attempts and settled Failed directly.
        Assert.Equal(1, controller.Plan!.Steps[0].Reworks);
    }

    [Fact]
    public void ValidateStep_Faulted_WithAttemptsLeft_SendsItBackToRework_ButDoesNotCountARework()
    {
        // Faulted means no verdict was ever reached (a crash, a stall, a refused isolation, a dead CEO) — this is the
        // AC-347 distinction a plain bool could not carry. The step still reworks (the driver re-runs it under the
        // attempt cap), but it must never be classified as a review finding later, so Reworks stays untouched.
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));
        controller.Approve();
        controller.StartStep("1"); // attempt 1

        Assert.True(controller.ValidateStep("1", AutopilotStepOutcome.Faulted, maxAttempts: 2));
        Assert.Equal(AutopilotStepStatus.Pending, controller.Plan!.Steps[0].Status);
        Assert.Equal(0, controller.Plan!.Steps[0].Reworks);
    }

    [Fact]
    public void ValidateStep_Faulted_WhenAttemptsAreExhausted_SettlesItFailed_BoundingTheLoop_WithNoReworkEver()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));
        controller.Approve();

        // Attempt 1 → faulted → back to Pending, no rework counted; attempt 2 → faulted → out of attempts → Failed.
        controller.StartStep("1");
        Assert.True(controller.ValidateStep("1", AutopilotStepOutcome.Faulted, maxAttempts: 2));
        controller.StartStep("1");
        Assert.False(controller.ValidateStep("1", AutopilotStepOutcome.Faulted, maxAttempts: 2));

        Assert.Equal(AutopilotStepStatus.Failed, controller.Plan!.Steps[0].Status);
        Assert.Equal(2, controller.Plan!.Steps[0].Attempts);
        Assert.Equal(0, controller.Plan!.Steps[0].Reworks);
        // Attempts > 1 with zero reworks is exactly the run-restart shape AutopilotCorrection.Classify reads.
        Assert.Equal(AutopilotCorrectionKind.RunRestart, AutopilotCorrection.Classify(controller.Plan!.Steps[0].Status, controller.Plan!.Steps[0].Attempts, controller.Plan!.Steps[0].Reworks));
    }

    [Fact]
    public void Block_Then_Resume_MovesThroughAwaitingOperator_BackToRunning()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));
        controller.Approve();

        controller.Block("Which region should this target?");
        Assert.Equal(AutopilotPlanPhase.AwaitingOperator, controller.Phase);
        Assert.Contains("region", controller.PendingQuestion);

        controller.ResumeRunning();
        Assert.Equal(AutopilotPlanPhase.Running, controller.Phase);
        Assert.Null(controller.PendingQuestion);
    }

    [Fact]
    public void Park_BlocksTheRun_WithTheReason()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));
        controller.Approve();

        controller.Park("No operator answer within the grace time.");

        Assert.Equal(AutopilotPlanPhase.Blocked, controller.Phase);
        Assert.Contains("grace time", controller.BlockReason);
    }

    [Fact]
    public void Stop_SettlesTheRun_Stopped_WithTheReason()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));
        controller.Approve();
        controller.StartStep("1");

        controller.Stop("Stopped by operator");

        Assert.Equal(AutopilotPlanPhase.Stopped, controller.Phase);
        Assert.Equal("Stopped by operator", controller.BlockReason);
        Assert.Null(controller.PendingQuestion);
    }

    [Fact]
    public void Stop_FromAwaitingOperator_ClearsThePendingQuestion_AndSettlesStopped()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));
        controller.Approve();
        controller.Block("Which region?");

        controller.Stop("Stopped by operator");

        Assert.Equal(AutopilotPlanPhase.Stopped, controller.Phase);
        Assert.Null(controller.PendingQuestion);
    }

    [Fact]
    public void Stop_RaisesChanged()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));
        controller.Approve();
        var count = 0;
        controller.Changed += (_, _) => count++;

        controller.Stop("Stopped by operator");

        Assert.Equal(1, count);
    }

    [Fact]
    public void Changed_Fires_OnPlanningAndStepTransitions()
    {
        var controller = new AutopilotPlanController();
        var count = 0;
        controller.Changed += (_, _) => count++;

        controller.BeginPlanning(PlanWith(Step("1")));
        controller.Approve();
        controller.StartStep("1");
        controller.SettleStep("1", AutopilotStepStatus.Passed);
        controller.Settle();

        Assert.Equal(5, count);
    }

    [Fact]
    public void RecordPullRequestMissing_SetsThePullRequestMissingFlag()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));

        controller.RecordPullRequestMissing();

        Assert.True(controller.PullRequestMissing);
    }

    [Fact]
    public void BeginPlanning_ResetsPullRequestMissing_FromAPriorRun()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));
        controller.Approve();
        controller.RecordPullRequestMissing();
        controller.SettleStep("1", AutopilotStepStatus.Passed);
        controller.Settle();

        controller.BeginPlanning(PlanWith(Step("2")));

        Assert.False(controller.PullRequestMissing);
    }

    [Fact]
    public void InsertStep_AppendsToTheLivingPlan_AndRaisesChanged()
    {
        // AC-434: how a review group's shared fix pass joins a plan the CEO never planned it into.
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));
        controller.Approve();
        var raised = 0;
        controller.Changed += (_, _) => raised++;

        controller.InsertStep(Step("review-fix-1"));

        Assert.Equal(["1", "review-fix-1"], controller.Plan!.Steps.Select(step => step.Id));
        Assert.Equal(1, raised);
    }

    [Fact]
    public void InsertStep_BeforeAPlanExists_IsANoOp()
    {
        var controller = new AutopilotPlanController();

        controller.InsertStep(Step("orphan"));

        Assert.Null(controller.Plan);
    }
}
