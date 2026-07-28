using FluentAssertions;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The AC-174 run driver's bounded loop: steps run in order, a failed step reworks up to the cap and then settles, a
/// hard failure blocks the run, and a step whose execution throws is a failed attempt rather than a crashed run.
/// <para>
/// AC-347: this is also where the <see cref="AutopilotStepOutcome.Rejected"/>/<see cref="AutopilotStepOutcome.Faulted"/>
/// distinction has to hold in the wired system, not just in a direct call to <see cref="AutopilotCorrection.Classify"/>.
/// Before this type existed, <c>executeStep</c> returned a plain <c>bool</c>, so every failed attempt — a genuine CEO
/// rejection or a session that crashed before any verdict — reworked the same way, which made <c>Reworks</c> always
/// equal <c>Attempts - 1</c> whenever a step ever left Pending, and left the <c>attempts &gt; 1</c> branch of
/// <see cref="AutopilotCorrection.Classify"/> unreachable through the driver. The two tests below run the actual
/// <see cref="AutopilotRunDriver"/> loop (not a hand-built state) to prove the branch is reachable.
/// </para>
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
            return Task.FromResult(AutopilotStepOutcome.Passed);
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

        await driver.RunAsync(_ => Task.FromResult(++runs >= 3 ? AutopilotStepOutcome.Passed : AutopilotStepOutcome.Rejected)); // fail, fail, pass

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
            return Task.FromResult(AutopilotStepOutcome.Rejected); // always fails
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
        // A thrown exception never reached a verdict — the driver's own catch converts it to Faulted, not Rejected.
        controller.Plan!.Steps[0].Reworks.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_AFaultedAttempt_ThenPassed_CountsTheAttemptButNotARework_AndClassifiesAsRunRestart()
    {
        // AC-347: the first attempt never reached a verdict (a crash, a stall, a refused isolation, a dead CEO — here
        // stood in for by Faulted), the retry passes. Attempts must be 2 (both starts counted) but Reworks must stay 0
        // (no verdict ever sent this step back) — the exact shape AutopilotCorrection.Classify reads as a run restart,
        // proven by running the actual driver loop rather than calling Classify with a hand-picked state.
        var controller = Approved(Step("1"));
        var attempt = 0;
        var driver = new AutopilotRunDriver(controller, maxAttempts: 2);

        await driver.RunAsync(_ =>
        {
            attempt++;
            return Task.FromResult(attempt == 1 ? AutopilotStepOutcome.Faulted : AutopilotStepOutcome.Passed);
        });

        var step = controller.Plan!.Steps[0];
        step.Status.Should().Be(AutopilotStepStatus.Passed);
        step.Attempts.Should().Be(2);
        step.Reworks.Should().Be(0);
        AutopilotCorrection.Classify(step.Status, step.Attempts, step.Reworks).Should().Be(AutopilotCorrectionKind.RunRestart);
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
        step.Status.Should().Be(AutopilotStepStatus.Passed);
        step.Attempts.Should().Be(2);
        step.Reworks.Should().Be(0);
        AutopilotCorrection.Classify(step.Status, step.Attempts, step.Reworks).Should().Be(AutopilotCorrectionKind.RunRestart);
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
        step.Status.Should().Be(AutopilotStepStatus.Passed);
        step.Attempts.Should().Be(2);
        step.Reworks.Should().Be(1);
        AutopilotCorrection.Classify(step.Status, step.Attempts, step.Reworks).Should().Be(AutopilotCorrectionKind.ReviewFinding);
    }
}
