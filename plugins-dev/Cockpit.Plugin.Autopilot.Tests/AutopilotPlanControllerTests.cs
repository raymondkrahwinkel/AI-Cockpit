namespace Cockpit.Plugin.Autopilot.Tests;

// The AC-174 plan controller: the planning round (living plan + single approval), the run driving its steps, and the
// settle that reads the per-step hard/skip policy — the plan-based counterpart of AutopilotRunControllerTests.
//
// The gate modes, statuses and outcomes are internal enums, so the data sources box them and each test casts back —
// a public test method may not name an internal type in its signature (CS0051), and xUnit1000 forbids making the
// class internal instead.
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
    public void StartStep_MarksItRunning_ExposesItAsActive_AndCountsEachRun()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1"), Step("2")));
        controller.Approve();

        controller.StartStep("1");

        Assert.Equal("1", controller.ActiveStep!.Id);
        Assert.Equal(AutopilotStepStatus.Running, controller.ActiveStep!.Status);
        Assert.Equal(1, controller.Plan!.Steps[0].Attempts);

        controller.StartStep("1");

        Assert.Equal(2, controller.Plan!.Steps[0].Attempts);
    }

    // What a settle makes of the per-step hard/skip policy. Only a hard step that did not pass blocks the run.
    public static IEnumerable<object[]> SettlesThatReachMergeReady() =>
    [
        [GateMode.Hard, AutopilotStepStatus.Passed, GateMode.Skip, AutopilotStepStatus.Passed],
        [GateMode.Hard, AutopilotStepStatus.Passed, GateMode.Skip, AutopilotStepStatus.Failed],
    ];

    [Theory]
    [MemberData(nameof(SettlesThatReachMergeReady))]
    public void Settle_WhenNoHardStepFailed_IsMergeReady(object firstMode, object firstStatus, object secondMode, object secondStatus)
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1", (GateMode)firstMode), Step("2", (GateMode)secondMode)));
        controller.Approve();
        controller.SettleStep("1", (AutopilotStepStatus)firstStatus);
        controller.SettleStep("2", (AutopilotStepStatus)secondStatus);

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

    // One validation round, with an attempt still in hand. `Rejected` is a genuine CEO verdict — the step's output was
    // judged against its acceptance and turned down — so it is the one outcome that counts as a rework (AC-347).
    // `Faulted` means no verdict was ever reached (a crash, a stall, a refused isolation, a dead CEO): the step still
    // reworks, but it must never be classified as a review finding later, so Reworks stays untouched.
    public static IEnumerable<object[]> ValidationRounds() =>
    [
        [AutopilotStepOutcome.Passed, false, AutopilotStepStatus.Passed, 0],
        [AutopilotStepOutcome.Rejected, true, AutopilotStepStatus.Pending, 1],
        [AutopilotStepOutcome.Faulted, true, AutopilotStepStatus.Pending, 0],
    ];

    [Theory]
    [MemberData(nameof(ValidationRounds))]
    public void ValidateStep_WithAnAttemptLeft_ReworksOnlyOnAVerdictAgainstIt(
        object outcome, bool reworked, object expectedStatus, int expectedReworks)
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));
        controller.Approve();
        controller.StartStep("1"); // attempt 1

        Assert.Equal(reworked, controller.ValidateStep("1", (AutopilotStepOutcome)outcome, maxAttempts: 2));
        Assert.Equal((AutopilotStepStatus)expectedStatus, controller.Plan!.Steps[0].Status);
        Assert.Equal(expectedReworks, controller.Plan!.Steps[0].Reworks);
    }

    // The attempt cap is what bounds the loop: the second turn-down settles the step Failed instead of sending it
    // back again. Only a rejection counted a rework on the way there; a fault never does.
    public static IEnumerable<object[]> ExhaustedValidations() =>
    [
        [AutopilotStepOutcome.Rejected, 1],
        [AutopilotStepOutcome.Faulted, 0],
    ];

    [Theory]
    [MemberData(nameof(ExhaustedValidations))]
    public void ValidateStep_WhenAttemptsAreExhausted_SettlesItFailed_BoundingTheLoop(object outcome, int expectedReworks)
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));
        controller.Approve();

        controller.StartStep("1");
        Assert.True(controller.ValidateStep("1", (AutopilotStepOutcome)outcome, maxAttempts: 2));
        controller.StartStep("1");
        Assert.False(controller.ValidateStep("1", (AutopilotStepOutcome)outcome, maxAttempts: 2));

        Assert.Equal(AutopilotStepStatus.Failed, controller.Plan!.Steps[0].Status);
        Assert.Equal(2, controller.Plan!.Steps[0].Attempts);
        Assert.Equal(expectedReworks, controller.Plan!.Steps[0].Reworks);
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
    public void Stop_FromAwaitingOperator_SettlesStopped_ClearsTheQuestion_AndRaisesChangedOnce()
    {
        // Stopped from the wait, because that is where the question exists to be cleared — from Running there is
        // nothing to clear and the assertion would pass on a controller that never clears it at all.
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));
        controller.Approve();
        controller.Block("Which region?");
        var count = 0;
        controller.Changed += (_, _) => count++;

        controller.Stop("Stopped by operator");

        Assert.Equal(AutopilotPlanPhase.Stopped, controller.Phase);
        Assert.Equal("Stopped by operator", controller.BlockReason);
        Assert.Null(controller.PendingQuestion);
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
    public void PullRequestMissing_IsRecordedOnTheRun_AndResetByTheNextPlanningRound()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));
        controller.Approve();

        controller.RecordPullRequestMissing();
        Assert.True(controller.PullRequestMissing);

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
