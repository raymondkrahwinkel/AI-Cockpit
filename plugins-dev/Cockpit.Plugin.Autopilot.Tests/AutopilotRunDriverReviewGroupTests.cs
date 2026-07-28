namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// AC-434: the driver's read-parallel/write-serial handling of a review-gate pair — <see cref="AutopilotRunDriverTests"/>
/// covers the ordinary one-step-at-a-time loop this builds on. Plain xUnit assertions (not FluentAssertions, which the
/// codebase is moving off — <c>CSharp.md</c> §FluentAssertions) since this is a new test file.
/// </summary>
public class AutopilotRunDriverReviewGroupTests
{
    private static AutopilotStep Gate(string id) =>
        new(id, $"Gate {id}", "d", "Claude", "Sonnet", "brief", "acc", GateMode.Hard) { IsReviewGate = true };

    private static AutopilotPlanController Approved(params AutopilotStep[] steps)
    {
        var controller = new AutopilotPlanController();
        controller.BeginPlanning(new AutopilotPlan("Goal", null, steps));
        controller.Approve();
        return controller;
    }

    [Fact]
    public async Task RunAsync_BothGatesCleanOnTheFirstRound_SettlesWithoutEverRunningAFixStep()
    {
        // AC4: a clean pair of gates costs nothing beyond the gates themselves.
        var controller = Approved(Gate("code-review"), Gate("security-review"));
        var driver = new AutopilotRunDriver(controller, maxAttempts: 3);
        var executed = new List<string>();

        await driver.RunAsync(step =>
        {
            lock (executed) { executed.Add(step.Id); }
            return Task.FromResult(AutopilotStepOutcome.Passed);
        });

        Assert.Equal(2, executed.Count);
        Assert.Contains("code-review", executed);
        Assert.Contains("security-review", executed);
        Assert.DoesNotContain(controller.Plan!.Steps, step => step.Id.StartsWith("review-fix-", StringComparison.Ordinal));
        Assert.Equal(AutopilotPlanPhase.MergeReady, controller.Phase);
    }

    [Fact]
    public async Task RunAsync_BothGates_ReadConcurrently_NotOneAfterAnother()
    {
        // AC1: the two gates' execution overlaps — neither waits for the other to finish before it even starts.
        var controller = Approved(Gate("code-review"), Gate("security-review"));
        var driver = new AutopilotRunDriver(controller, maxAttempts: 1);
        var started = new TaskCompletionSource<string>[2];
        started[0] = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        started[1] = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = 0;

        await driver.RunAsync(async step =>
        {
            var index = Interlocked.Increment(ref next) - 1;
            started[index].TrySetResult(step.Id);
            // Only resolves once BOTH have started — a sequential driver would hang here forever on the second slot.
            await Task.WhenAll(started[0].Task, started[1].Task).WaitAsync(TimeSpan.FromSeconds(5));
            return AutopilotStepOutcome.Passed;
        });

        Assert.Equal(AutopilotPlanPhase.MergeReady, controller.Phase);
    }

    [Fact]
    public async Task RunAsync_OneGateRejects_InsertsASharedFixStep_ThenOnlyReRunsTheRejectedGate()
    {
        // AC2/AC3/AC4: the clean gate never runs again; the fix step carries the rejected gate's reason; both gates
        // keep their own verdict (the clean one settles Passed on round 1, never touched again).
        var controller = Approved(Gate("code-review"), Gate("security-review"));
        var driver = new AutopilotRunDriver(controller, maxAttempts: 3);
        var runs = new Dictionary<string, int>();

        await driver.RunAsync(step =>
        {
            lock (runs) { runs[step.Id] = runs.GetValueOrDefault(step.Id) + 1; }

            if (step.Id == "code-review")
            {
                controller.NoteStep(step.Id, "found an untrue comment");
                return Task.FromResult(runs["code-review"] == 1 ? AutopilotStepOutcome.Rejected : AutopilotStepOutcome.Passed);
            }

            if (step.Id == "security-review")
            {
                return Task.FromResult(AutopilotStepOutcome.Passed);
            }

            // The synthesized fix step.
            return Task.FromResult(AutopilotStepOutcome.Passed);
        });

        Assert.Equal(2, runs["code-review"]); // rejected once, then re-verified after the fix
        Assert.Equal(1, runs["security-review"]); // clean on round 1 — never reran
        var fixStep = Assert.Single(controller.Plan!.Steps, step => step.Id.StartsWith("review-fix-", StringComparison.Ordinal));
        Assert.Contains("found an untrue comment", fixStep.Brief);
        Assert.DoesNotContain("security-review", fixStep.Brief, StringComparison.Ordinal);
        Assert.Equal(AutopilotStepStatus.Passed, controller.Plan.Steps.First(step => step.Id == "code-review").Status);
        Assert.Equal(AutopilotPlanPhase.MergeReady, controller.Phase);
    }

    [Fact]
    public async Task RunAsync_TheFixStepNeverLands_FailsTheStillOpenGates_AndBlocksTheRun()
    {
        // The fix pass is bounded by the same attempt cap as any step; if it never lands, the gates it was fixing for
        // cannot be re-verified against a fix that never happened, so the run blocks on them instead of looping forever.
        var controller = Approved(Gate("code-review"), Gate("security-review"));
        var driver = new AutopilotRunDriver(controller, maxAttempts: 2);
        var gateRuns = 0;

        await driver.RunAsync(step =>
        {
            if (step.Id is "code-review" or "security-review")
            {
                Interlocked.Increment(ref gateRuns);
                return Task.FromResult(AutopilotStepOutcome.Rejected);
            }

            // The fix step: always rejected too, so it exhausts its own cap without ever landing.
            return Task.FromResult(AutopilotStepOutcome.Rejected);
        });

        Assert.Equal(2, gateRuns); // each gate ran once (round 1), rejected, and never got a re-verify — the fix never landed
        Assert.Equal(AutopilotStepStatus.Failed, controller.Plan!.Steps.First(step => step.Id == "code-review").Status);
        Assert.Equal(AutopilotStepStatus.Failed, controller.Plan.Steps.First(step => step.Id == "security-review").Status);
        Assert.Equal(AutopilotPlanPhase.Blocked, controller.Phase);
    }

    [Fact]
    public async Task RunAsync_ASingleReviewGate_StillGetsASharedFixStep_OnRejection()
    {
        // Adversarial-review fix: the CEO may drop one gate (its own brief allows this for a trivial/docs-only run),
        // leaving exactly one IsReviewGate step pending. It must still go through the fix-step machinery — not the
        // plain single-step rework loop, whose rework would run in a throwaway worktree and fix nothing real.
        var controller = Approved(Gate("code-review"));
        var driver = new AutopilotRunDriver(controller, maxAttempts: 2);
        var runs = 0;

        await driver.RunAsync(step =>
        {
            if (step.Id == "code-review")
            {
                runs++;
                controller.NoteStep(step.Id, "found an untrue comment");
                return Task.FromResult(runs == 1 ? AutopilotStepOutcome.Rejected : AutopilotStepOutcome.Passed);
            }

            return Task.FromResult(AutopilotStepOutcome.Passed); // the synthesized fix step
        });

        Assert.Equal(2, runs);
        Assert.Single(controller.Plan!.Steps, step => step.Id.StartsWith("review-fix-", StringComparison.Ordinal));
        Assert.Equal(AutopilotStepStatus.Passed, controller.Plan.Steps.First(step => step.Id == "code-review").Status);
        Assert.Equal(AutopilotPlanPhase.MergeReady, controller.Phase);
    }

    [Fact]
    public async Task RunAsync_CancelledMidGroup_LeavesTheGatesAsValidateStepLeftThem_RatherThanForcingFailed()
    {
        // Adversarial-review fix: a stopped/cancelled run must not read back as "both gates failed" — that never
        // happened, nobody judged the work. Only a genuinely exhausted fix pass may fail the gates it was fixing for.
        var controller = Approved(Gate("code-review"), Gate("security-review"));
        var driver = new AutopilotRunDriver(controller, maxAttempts: 3);
        using var cts = new CancellationTokenSource();

        await driver.RunAsync(step =>
        {
            if (step.Id is "code-review" or "security-review")
            {
                return Task.FromResult(AutopilotStepOutcome.Rejected);
            }

            // The fix step starts, then the run is cancelled mid-flight (before it ever settles).
            cts.Cancel();
            return Task.FromResult(AutopilotStepOutcome.Rejected);
        }, cts.Token);

        Assert.Equal(AutopilotStepStatus.Pending, controller.Plan!.Steps.First(step => step.Id == "code-review").Status);
        Assert.Equal(AutopilotStepStatus.Pending, controller.Plan.Steps.First(step => step.Id == "security-review").Status);
    }

    [Fact]
    public async Task RunAsync_AFaultedOnlyRound_RetriesDirectly_WithoutSynthesizingAFixStep()
    {
        // Adversarial-review fix: a Faulted gate (a crash, a stall — no verdict) is a run restart, never a review
        // finding (AC-347) — it must not be fed into a fix step's brief as if it were one, and a round where nothing
        // was genuinely rejected must not synthesize a fix step at all.
        var controller = Approved(Gate("code-review"));
        var driver = new AutopilotRunDriver(controller, maxAttempts: 2);
        var runs = 0;

        await driver.RunAsync(_ =>
        {
            runs++;
            return Task.FromResult(runs == 1 ? AutopilotStepOutcome.Faulted : AutopilotStepOutcome.Passed);
        });

        Assert.Equal(2, runs);
        Assert.DoesNotContain(controller.Plan!.Steps, step => step.Id.StartsWith("review-fix-", StringComparison.Ordinal));
        Assert.Equal(AutopilotStepStatus.Passed, controller.Plan.Steps.First(step => step.Id == "code-review").Status);
    }

    [Fact]
    public async Task RunAsync_OneGateRejectsAndOneFaults_TheFixBriefOnlyCarriesTheGenuineFinding()
    {
        // A mixed round: the faulted gate's crash text must never read as a review finding in the fix brief, but it
        // is still retried once the fix (for the OTHER, genuinely rejected gate) lands.
        var controller = Approved(Gate("code-review"), Gate("security-review"));
        var driver = new AutopilotRunDriver(controller, maxAttempts: 3);
        var runs = new Dictionary<string, int>();

        await driver.RunAsync(step =>
        {
            lock (runs) { runs[step.Id] = runs.GetValueOrDefault(step.Id) + 1; }

            if (step.Id == "code-review")
            {
                controller.NoteStep(step.Id, "found an untrue comment");
                return Task.FromResult(runs["code-review"] == 1 ? AutopilotStepOutcome.Rejected : AutopilotStepOutcome.Passed);
            }

            if (step.Id == "security-review")
            {
                // Faults on round 1 (no verdict — a crashed session), then passes on retry.
                return Task.FromResult(runs["security-review"] == 1 ? AutopilotStepOutcome.Faulted : AutopilotStepOutcome.Passed);
            }

            return Task.FromResult(AutopilotStepOutcome.Passed); // the synthesized fix step
        });

        var fixStep = Assert.Single(controller.Plan!.Steps, step => step.Id.StartsWith("review-fix-", StringComparison.Ordinal));
        Assert.Contains("found an untrue comment", fixStep.Brief);
        Assert.DoesNotContain("Gate security-review", fixStep.Brief, StringComparison.Ordinal);
        Assert.Equal(2, runs["security-review"]); // faulted once, retried directly — no fix step depended on it
        Assert.Equal(AutopilotPlanPhase.MergeReady, controller.Phase);
    }

    [Fact]
    public async Task RunAsync_TwoNonAdjacentReviewGateGroups_ProduceDistinctFixSteps_NoIdCollision()
    {
        // Adversarial-review fix: AC-434's contiguity rule (AutopilotPlan.NextPendingGroup) can turn two non-adjacent
        // review gates into two SEPARATE single-gate groups. Each group's _RunReviewGroupAsync starts counting
        // rounds at 1 independently — the fix step's id must stay unique across both, or the second group's fix step
        // silently reads (and overwrites) the first's.
        var implement = new AutopilotStep("implement", "Implement", "d", "Claude", "Sonnet", "brief", "acc", GateMode.Hard, AutopilotStepStatus.Passed);
        var controller = Approved(Gate("gate-a"), implement, Gate("gate-b"));
        var driver = new AutopilotRunDriver(controller, maxAttempts: 3);
        var runs = new Dictionary<string, int>();

        await driver.RunAsync(step =>
        {
            lock (runs) { runs[step.Id] = runs.GetValueOrDefault(step.Id) + 1; }

            if (step.Id == "gate-a")
            {
                controller.NoteStep(step.Id, "gate-a finding");
                return Task.FromResult(runs["gate-a"] == 1 ? AutopilotStepOutcome.Rejected : AutopilotStepOutcome.Passed);
            }

            if (step.Id == "gate-b")
            {
                controller.NoteStep(step.Id, "gate-b finding");
                return Task.FromResult(runs["gate-b"] == 1 ? AutopilotStepOutcome.Rejected : AutopilotStepOutcome.Passed);
            }

            return Task.FromResult(AutopilotStepOutcome.Passed); // either group's fix step
        });

        var fixSteps = controller.Plan!.Steps.Where(step => step.Id.StartsWith("review-fix-", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, fixSteps.Count);
        Assert.NotEqual(fixSteps[0].Id, fixSteps[1].Id);
        Assert.Contains(fixSteps, step => step.Brief.Contains("gate-a finding"));
        Assert.Contains(fixSteps, step => step.Brief.Contains("gate-b finding"));
        Assert.Equal(2, runs["gate-a"]);
        Assert.Equal(2, runs["gate-b"]);
        Assert.Equal(AutopilotPlanPhase.MergeReady, controller.Phase);
    }
}
