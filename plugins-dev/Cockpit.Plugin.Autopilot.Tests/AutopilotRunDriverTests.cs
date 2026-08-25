namespace Cockpit.Plugin.Autopilot.Tests;

// The AC-174 run driver's bounded loop: steps run in order, a failed step reworks up to the cap and then settles,
// a hard failure blocks the run, and a throwing step is a failed attempt rather than a crashed run. AC-347: also
// where Rejected/Faulted must hold in the wired system — `executeStep` used to return a plain `bool`.
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
            return Task.FromResult(AutopilotStepOutcome.Passed);
        });

        Assert.Equal(new[] { "1", "2" }, order);
        Assert.Equal(AutopilotPlanPhase.MergeReady, controller.Phase);
        Assert.All(controller.Plan!.Steps, step => Assert.Equal(AutopilotStepStatus.Passed, step.Status));
    }

    [Fact]
    public async Task RunAsync_ReworksAFailingStep_UntilItPasses()
    {
        var controller = Approved(Step("1"));
        var runs = 0;
        var driver = new AutopilotRunDriver(controller, maxAttempts: 3);

        await driver.RunAsync(_ => Task.FromResult(++runs >= 3 ? AutopilotStepOutcome.Passed : AutopilotStepOutcome.Rejected)); // fail, fail, pass

        Assert.Equal(3, runs);
        Assert.Equal(AutopilotStepStatus.Passed, controller.Plan!.Steps[0].Status);
        Assert.Equal(AutopilotPlanPhase.MergeReady, controller.Phase);
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
            return Task.FromResult(AutopilotStepOutcome.Rejected); // always fails
        });

        Assert.Equal(2, runs); // exactly the cap — no endless loop
        Assert.Equal(AutopilotStepStatus.Failed, controller.Plan!.Steps[0].Status);
        Assert.Equal(AutopilotPlanPhase.Blocked, controller.Phase);
    }

    [Fact]
    public async Task RunAsync_AThrowingStep_CountsAsAFailedAttempt_NotACrash()
    {
        var controller = Approved(Step("1"));
        var driver = new AutopilotRunDriver(controller, maxAttempts: 1);

        var act = () => driver.RunAsync(_ => throw new InvalidOperationException("boom"));

        await act();
        Assert.Equal(AutopilotStepStatus.Failed, controller.Plan!.Steps[0].Status);
        // A thrown exception never reached a verdict — the driver's own catch converts it to Faulted, not Rejected.
        Assert.Equal(0, controller.Plan!.Steps[0].Reworks);
    }

    [Fact]
    public async Task RunAsync_AFaultedAttempt_ThenPassed_CountsTheAttemptButNotARework_AndClassifiesAsRunRestart()
    {
        // AC-347: the first attempt never reached a verdict (stood in for by Faulted), the retry passes. Attempts
        // must be 2 but Reworks must stay 0 — the exact shape `Classify` reads as a run restart, proven by running
        // the actual driver loop rather than calling Classify with a hand-picked state.
        var controller = Approved(Step("1"));
        var attempt = 0;
        var driver = new AutopilotRunDriver(controller, maxAttempts: 2);

        await driver.RunAsync(_ =>
        {
            attempt++;
            return Task.FromResult(attempt == 1 ? AutopilotStepOutcome.Faulted : AutopilotStepOutcome.Passed);
        });

        var step = controller.Plan!.Steps[0];
        Assert.Equal(AutopilotStepStatus.Passed, step.Status);
        Assert.Equal(2, step.Attempts);
        Assert.Equal(0, step.Reworks);
        Assert.Equal(AutopilotCorrectionKind.RunRestart, AutopilotCorrection.Classify(step.Status, step.Attempts, step.Reworks));
    }

    [Fact]
    public async Task RunAsync_AThrownFirstAttempt_ThenPassed_CountsTheAttemptButNotARework_AndClassifiesAsRunRestart()
    {
        // The mirror of the Faulted-return case above, but produced the other way Faulted can arise: the driver's own
        // catch around executeStep, not a value the fake returns.
        var controller = Approved(Step("1"));
        var attempt = 0;
        var driver = new AutopilotRunDriver(controller, maxAttempts: 2);

        await driver.RunAsync(_ =>
        {
            attempt++;
            if (attempt == 1)
            {
                throw new InvalidOperationException("the step agent's session ended before it reported its work done.");
            }

            return Task.FromResult(AutopilotStepOutcome.Passed);
        });

        var step = controller.Plan!.Steps[0];
        Assert.Equal(AutopilotStepStatus.Passed, step.Status);
        Assert.Equal(2, step.Attempts);
        Assert.Equal(0, step.Reworks);
        Assert.Equal(AutopilotCorrectionKind.RunRestart, AutopilotCorrection.Classify(step.Status, step.Attempts, step.Reworks));
    }

    [Fact]
    public async Task RunAsync_ARejectedAttempt_ThenPassed_CountsBothTheAttemptAndARework_AndClassifiesAsReviewFinding()
    {
        // The mirror image: the first attempt DID reach a verdict — the CEO judged it and turned it down — and the
        // retry passes. Attempts is 2 and Reworks is 1, the shape Classify reads as a review finding.
        var controller = Approved(Step("1"));
        var attempt = 0;
        var driver = new AutopilotRunDriver(controller, maxAttempts: 2);

        await driver.RunAsync(_ =>
        {
            attempt++;
            return Task.FromResult(attempt == 1 ? AutopilotStepOutcome.Rejected : AutopilotStepOutcome.Passed);
        });

        var step = controller.Plan!.Steps[0];
        Assert.Equal(AutopilotStepStatus.Passed, step.Status);
        Assert.Equal(2, step.Attempts);
        Assert.Equal(1, step.Reworks);
        Assert.Equal(AutopilotCorrectionKind.ReviewFinding, AutopilotCorrection.Classify(step.Status, step.Attempts, step.Reworks));
    }
}
