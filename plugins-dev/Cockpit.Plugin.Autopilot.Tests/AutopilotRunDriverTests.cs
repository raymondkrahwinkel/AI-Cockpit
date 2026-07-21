using FluentAssertions;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The AC-174 run driver's bounded loop: steps run in order, a failed step reworks up to the cap and then settles, a
/// hard failure blocks the run, and a step whose execution throws is a failed attempt rather than a crashed run.
/// </summary>
public class AutopilotRunDriverTests
{
    private static AutopilotStep Step(string id, GateMode mode = GateMode.Skip) =>
        new(id, $"Step {id}", "d", "Claude", "Sonnet", "brief", "acc", mode);

    private static AutopilotPlanController Approved(params AutopilotStep[] steps)
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(new AutopilotPlan("Goal", null, steps));
        controller.Approve();
        return controller;
    }

    [Fact]
    public async Task RunAsync_WhenEveryStepPasses_RunsThemInOrder_AndSettlesMergeReady()
    {
        var controller = Approved(Step("1"), Step("2"));
        var order = new List<string>();
        var driver = new AutopilotRunDriver(controller, maxAttempts: 2);

        await driver.RunAsync(step =>
        {
            order.Add(step.Id);
            return Task.FromResult(true);
        });

        order.Should().Equal("1", "2");
        controller.Phase.Should().Be(AutopilotPlanPhase.MergeReady);
        controller.Plan!.Steps.Should().OnlyContain(step => step.Status == AutopilotStepStatus.Passed);
    }

    [Fact]
    public async Task RunAsync_ReworksAFailingStep_UntilItPasses()
    {
        var controller = Approved(Step("1"));
        var runs = 0;
        var driver = new AutopilotRunDriver(controller, maxAttempts: 3);

        await driver.RunAsync(_ => Task.FromResult(++runs >= 3)); // fail, fail, pass

        runs.Should().Be(3);
        controller.Plan!.Steps[0].Status.Should().Be(AutopilotStepStatus.Passed);
        controller.Phase.Should().Be(AutopilotPlanPhase.MergeReady);
    }

    [Fact]
    public async Task RunAsync_BoundsReworkAtTheCap_ThenBlocksOnAHardFailure()
    {
        var controller = Approved(Step("1", GateMode.Hard));
        var runs = 0;
        var driver = new AutopilotRunDriver(controller, maxAttempts: 2);

        await driver.RunAsync(_ =>
        {
            runs++;
            return Task.FromResult(false); // always fails
        });

        runs.Should().Be(2); // exactly the cap — no endless loop
        controller.Plan!.Steps[0].Status.Should().Be(AutopilotStepStatus.Failed);
        controller.Phase.Should().Be(AutopilotPlanPhase.Blocked);
    }

    [Fact]
    public async Task RunAsync_AThrowingStep_CountsAsAFailedAttempt_NotACrash()
    {
        var controller = Approved(Step("1"));
        var driver = new AutopilotRunDriver(controller, maxAttempts: 1);

        var act = () => driver.RunAsync(_ => throw new InvalidOperationException("boom"));

        await act.Should().NotThrowAsync();
        controller.Plan!.Steps[0].Status.Should().Be(AutopilotStepStatus.Failed);
    }
}
