using Cockpit.Plugins.Abstractions;
using NSubstitute;

namespace Cockpit.Plugin.Autopilot.Tests;

// The AC-347 blockade-answer counter on `AutopilotPlanController`: `AutopilotPlanController.RecordBlockadeAnswer`
// is the only way it goes up, a fresh planning round resets it, and calling `AutopilotPlanController.ResumeRunning`
// alone — without the explicit record call — must not count anything.
public class AutopilotPlanControllerBlockadeTests
{
    private static AutopilotStep Step(string id) => new(id, $"Step {id}", "desc", "Claude", "Sonnet", "brief", "acceptance");

    private static AutopilotPlan PlanWith(params AutopilotStep[] steps) => new("Goal", null, steps);

    [Fact]
    public void RecordBlockadeAnswer_IncrementsTheCount()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));
        controller.Approve();

        controller.RecordBlockadeAnswer();
        controller.RecordBlockadeAnswer();

        Assert.Equal(2, controller.BlockadeAnswers);
    }

    [Fact]
    public void BeginPlanning_ResetsTheCountToZero()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));
        controller.Approve();
        controller.RecordBlockadeAnswer();
        controller.SettleStep("1", AutopilotStepStatus.Passed);
        controller.Settle();

        controller.BeginPlanning(PlanWith(Step("2")));

        Assert.Equal(0, controller.BlockadeAnswers);
    }

    [Fact]
    public void ResumeRunning_Alone_DoesNotIncrementTheCount()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));
        controller.Approve();
        controller.Block("Which region?");

        controller.ResumeRunning();

        Assert.Equal(0, controller.BlockadeAnswers);
    }

    // The one place an operator actually answers a blockade: through the coordinator, not the controller
    // directly. This is what proves AnswerBlockadeAsync itself calls RecordBlockadeAnswer, not just that the counter
    // works in isolation.
    [Fact]
    public async Task AnswerBlockadeAsync_OnTheCoordinator_CountsAsABlockadeAnswer()
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(PlanWith(Step("1")));
        controller.Approve();
        controller.Block("Which region?");

        var coordinator = new AutopilotRunCoordinator(Substitute.For<ICockpitHost>(), controller);
        await coordinator.AnswerBlockadeAsync("go ahead");

        Assert.Equal(1, controller.BlockadeAnswers);
        Assert.Equal(AutopilotPlanPhase.Running, controller.Phase);
    }
}
