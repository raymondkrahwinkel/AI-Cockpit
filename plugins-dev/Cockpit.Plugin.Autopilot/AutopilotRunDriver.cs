namespace Cockpit.Plugin.Autopilot;

// Drives an approved plan to completion (AC-174) — the bounded agentic loop: pick the next pending step, run it,
// have the CEO validate it, and either advance, rework, or give up once the attempt cap is hit, then settle merge-
// ready or blocked. The actual work of a step is injected as `executeStep`, so this stays a pure, testable loop.
internal sealed class AutopilotRunDriver(
    AutopilotPlanController controller,
    int maxAttempts,
    Func<AutopilotStep, Task<AutopilotStep>>? holdToCostCeiling = null)
{
    // Runs the plan: starts/executes/validates each pending step, reworking until it passes or attempts run out,
    // settling once no step is pending. `Rejected` and `Faulted` both rework/bound the same way, but only the
    // former is a review finding. AC-434: a review-gate pair runs concurrently via `_RunReviewGroupAsync`.
    public async Task RunAsync(Func<AutopilotStep, Task<AutopilotStepOutcome>> executeStep, CancellationToken cancellationToken = default)
    {
        while (controller.Phase == AutopilotPlanPhase.Running && !cancellationToken.IsCancellationRequested)
        {
            var group = controller.Plan?.NextPendingGroup ?? [];
            if (group.Count == 0)
            {
                controller.Settle();
                return;
            }

            // AC-434: a single pending review-gate step still runs through _RunReviewGroupAsync rather than the plain
            // single-step path — a lone gate's rejection still needs the shared fix step, not a rework that fixes
            // nothing (its own worktree is a throwaway fork, never the run's real one).
            if (group.Count == 1 && !group[0].IsReviewGate)
            {
                await _DriveStepToSettledAsync(group[0], executeStep, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await _RunReviewGroupAsync(group, executeStep, cancellationToken).ConfigureAwait(false);
        }
    }

    // Runs one step through start/execute/validate, reworking it while ValidateStep says to, until it settles or
    // the run leaves Running. Extracted so a review group's shared fix pass (AC-434) drives its synthesized step
    // through the identical cycle an ordinary plan step gets.
    private async Task _DriveStepToSettledAsync(AutopilotStep step, Func<AutopilotStep, Task<AutopilotStepOutcome>> executeStep, CancellationToken cancellationToken)
    {
        var stepId = step.Id;
        while (controller.Phase == AutopilotPlanPhase.Running && !cancellationToken.IsCancellationRequested)
        {
            controller.StartStep(stepId);

            // The current step object carries the attempt just recorded and this run's profile/model/MCP.
            var current = controller.Plan?.Steps.FirstOrDefault(candidate => candidate.Id == stepId) ?? step;

            AutopilotStepOutcome outcome;
            try
            {
                outcome = await executeStep(current).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A step whose execution threw is a failed attempt, not a crashed run — it reworks or gives up like any
                // other failure, so a flaky session or a broken profile cannot take the whole run down. It never saw a
                // verdict, so it is Faulted, not Rejected — a thrown exception is not the CEO turning the step down.
                outcome = AutopilotStepOutcome.Faulted;
            }

            // A rework leaves the step Pending, so this loop picks it up again (StartStep records the next attempt); a
            // settled step (passed, or failed out of attempts) is done here — the caller decides what happens next.
            if (!controller.ValidateStep(stepId, outcome, maxAttempts))
            {
                return;
            }
        }
    }

    // AC-434: read-parallel, write-serial. Every gate still open this round runs concurrently in its own throwaway
    // worktree. When at least one gate is genuinely REJECTED, one shared fix step applies every finding before
    // re-verifying; a gate that only FAULTED is not a finding and is simply retried next round.
    private async Task _RunReviewGroupAsync(IReadOnlyList<AutopilotStep> group, Func<AutopilotStep, Task<AutopilotStepOutcome>> executeStep, CancellationToken cancellationToken)
    {
        var lead = group[0];
        var open = group;
        var round = 0;

        while (open.Count > 0 && controller.Phase == AutopilotPlanPhase.Running && !cancellationToken.IsCancellationRequested)
        {
            foreach (var step in open)
            {
                controller.StartStep(step.Id);
            }

            var results = await Task.WhenAll(open.Select(async step =>
            {
                var current = controller.Plan?.Steps.FirstOrDefault(candidate => candidate.Id == step.Id) ?? step;
                AutopilotStepOutcome outcome;
                try
                {
                    outcome = await executeStep(current).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    outcome = AutopilotStepOutcome.Faulted;
                }

                return (step.Id, Outcome: outcome, NeedsRework: controller.ValidateStep(step.Id, outcome, maxAttempts));
            })).ConfigureAwait(false);

            // Read back from the live plan, not the group snapshot from before this round ran — ValidateStep's rework
            // leaves the gate's rejection note on it (plan.NoteStep, called by the real executeStep adapter), and the
            // fix step built below needs that note, not the blank one the step started this round with.
            AutopilotStep _Current(string id) => controller.Plan?.Steps.FirstOrDefault(step => step.Id == id) ?? group.First(step => step.Id == id);

            open = [.. results.Where(result => result.NeedsRework).Select(result => _Current(result.Id))];
            if (open.Count == 0)
            {
                return;
            }

            IReadOnlyList<AutopilotStep> rejected =
                [.. results.Where(result => result.NeedsRework && result.Outcome == AutopilotStepOutcome.Rejected).Select(result => _Current(result.Id))];
            if (rejected.Count == 0)
            {
                // Every gate needing rework this round only faulted — nothing was actually found, so there is nothing
                // for a fix pass to apply. Loop back and just retry them; a fix step never gets synthesized for a
                // round with no genuine finding in it.
                continue;
            }

            round++;
            var fixStep = AutopilotReviewFixStep.Build(lead, rejected, round);

            // AC-256: this step inherits the gate's profile and model, and a review gate is exempt from the run's
            // cost ceiling — without this, an expensive reviewer model would carry straight into the code-writing
            // step. Null in a bare test graph, where there is no roster to hold anything to.
            if (holdToCostCeiling is not null)
            {
                try
                {
                    fixStep = await holdToCostCeiling(fixStep).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Reading the profile roster is the only thing that can fault here, same best-effort treatment
                    // as the coordinator's own roster reads: a fix pass costing a tier more beats the run dying
                    // silently because a config file was briefly unreadable.
                }
            }

            controller.InsertStep(fixStep);
            await _DriveStepToSettledAsync(fixStep, executeStep, cancellationToken).ConfigureAwait(false);

            var fixSettled = controller.Plan?.Steps.FirstOrDefault(candidate => candidate.Id == fixStep.Id);
            if (fixSettled?.Status == AutopilotStepStatus.Failed)
            {
                // The fix pass itself exhausted its attempts — the gates it was fixing for cannot be re-verified
                // against a fix that never landed, so fail those here instead of looping on a fix that will not come.
                // A gate that only faulted this round (not in `rejected`) is unrelated to this fix and keeps retrying.
                foreach (var step in rejected)
                {
                    controller.SettleStep(step.Id, AutopilotStepStatus.Failed);
                }

                open = [.. open.Where(step => rejected.All(failed => failed.Id != step.Id))];
            }
            else if (fixSettled?.Status != AutopilotStepStatus.Passed)
            {
                // Neither Passed nor Failed: the run left Running or was cancelled mid-fix, not a genuine exhaustion —
                // leave every gate exactly as ValidateStep already left it rather than fabricating a Failed verdict.
                return;
            }
        }
    }
}
