using FluentAssertions;

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

        controller.Phase.Should().Be(AutopilotPlanPhase.Planning);
        controller.Plan.Should().BeSameAs(plan);
    }

    [Fact]
    public void UpdatePlan_ReplacesTheLivingPlan_DuringPlanning()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));

        var revised = PlanWith(Step("1"), Step("2"));
        controller.UpdatePlan(revised);

        controller.Plan.Should().BeSameAs(revised);
        controller.Phase.Should().Be(AutopilotPlanPhase.Planning);
    }

    [Fact]
    public void Approve_AnEmptyPlan_IsRefused_AndStaysInPlanning()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(AutopilotPlan.Empty(null, "Goal"));

        controller.Approve().Should().BeFalse();
        controller.Phase.Should().Be(AutopilotPlanPhase.Planning);
    }

    [Fact]
    public void Approve_WithSteps_FreezesThePlan_AndStartsRunning()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));

        controller.Approve().Should().BeTrue();
        controller.Phase.Should().Be(AutopilotPlanPhase.Running);
    }

    [Fact]
    public void BeginPlanning_WhileARunIsLive_IsRefused_LeavingTheRunUntouched()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1"))).Should().BeTrue();
        controller.Approve();

        controller.BeginPlanning(PlanWith(Step("other"))).Should().BeFalse();
        controller.Phase.Should().Be(AutopilotPlanPhase.Running);
        controller.Plan!.Steps.Should().ContainSingle().Which.Id.Should().Be("1");
    }

    [Fact]
    public void BeginPlanning_AfterASettledRun_StartsFresh()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));
        controller.Approve();
        controller.SettleStep("1", AutopilotStepStatus.Passed);
        controller.Settle();

        controller.BeginPlanning(PlanWith(Step("2"))).Should().BeTrue();
        controller.Phase.Should().Be(AutopilotPlanPhase.Planning);
    }

    [Fact]
    public void StartStep_MarksItRunning_AndExposesItAsActive()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1"), Step("2")));
        controller.Approve();

        controller.StartStep("1");

        controller.ActiveStep!.Id.Should().Be("1");
        controller.ActiveStep!.Status.Should().Be(AutopilotStepStatus.Running);
    }

    [Fact]
    public void Settle_WhenEveryHardStepPassed_IsMergeReady()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1", GateMode.Hard), Step("2")));
        controller.Approve();
        controller.SettleStep("1", AutopilotStepStatus.Passed);
        controller.SettleStep("2", AutopilotStepStatus.Passed);

        controller.AllSettled.Should().BeTrue();
        controller.Settle();

        controller.Phase.Should().Be(AutopilotPlanPhase.MergeReady);
        controller.BlockReason.Should().BeNull();
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

        controller.Phase.Should().Be(AutopilotPlanPhase.Blocked);
        controller.BlockReason.Should().Contain("Step 1");
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

        controller.Phase.Should().Be(AutopilotPlanPhase.MergeReady);
    }

    [Fact]
    public void StartStep_RecordsAnAttempt_EachTimeItRuns()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));
        controller.Approve();

        controller.StartStep("1");
        controller.Plan!.Steps[0].Attempts.Should().Be(1);

        controller.StartStep("1");
        controller.Plan!.Steps[0].Attempts.Should().Be(2);
    }

    [Fact]
    public void ValidateStep_WhenPassed_SettlesTheStep_AndDoesNotRework()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));
        controller.Approve();
        controller.StartStep("1");

        controller.ValidateStep("1", passed: true, maxAttempts: 2).Should().BeFalse();
        controller.Plan!.Steps[0].Status.Should().Be(AutopilotStepStatus.Passed);
    }

    [Fact]
    public void ValidateStep_OnFail_WithAttemptsLeft_SendsItBackToRework()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));
        controller.Approve();
        controller.StartStep("1"); // attempt 1

        controller.ValidateStep("1", passed: false, maxAttempts: 2).Should().BeTrue();
        controller.Plan!.Steps[0].Status.Should().Be(AutopilotStepStatus.Pending);
    }

    [Fact]
    public void ValidateStep_OnFail_WhenAttemptsAreExhausted_SettlesItFailed_BoundingTheLoop()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));
        controller.Approve();

        // Attempt 1 → fail → rework; attempt 2 → fail → out of attempts → Failed, no more rework.
        controller.StartStep("1");
        controller.ValidateStep("1", passed: false, maxAttempts: 2).Should().BeTrue();
        controller.StartStep("1");
        controller.ValidateStep("1", passed: false, maxAttempts: 2).Should().BeFalse();

        controller.Plan!.Steps[0].Status.Should().Be(AutopilotStepStatus.Failed);
        controller.Plan!.Steps[0].Attempts.Should().Be(2);
    }

    [Fact]
    public void Block_Then_Resume_MovesThroughAwaitingOperator_BackToRunning()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));
        controller.Approve();

        controller.Block("Which region should this target?");
        controller.Phase.Should().Be(AutopilotPlanPhase.AwaitingOperator);
        controller.PendingQuestion.Should().Contain("region");

        controller.ResumeRunning();
        controller.Phase.Should().Be(AutopilotPlanPhase.Running);
        controller.PendingQuestion.Should().BeNull();
    }

    [Fact]
    public void Park_BlocksTheRun_WithTheReason()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));
        controller.Approve();

        controller.Park("No operator answer within the grace time.");

        controller.Phase.Should().Be(AutopilotPlanPhase.Blocked);
        controller.BlockReason.Should().Contain("grace time");
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

        count.Should().Be(5);
    }
}
