namespace Cockpit.Plugin.Autopilot;

// Drives an approved plan to completion (AC-174) — the bounded agentic loop: pick the next pending
// step, run it, have the CEO validate its output against the step's acceptance, and either advance, send it back to
// rework (re-run), or give up once the attempt cap is hit — then settle the run merge-ready or blocked.
//
// The actual work of a step — embedding a session on the step's profile/model/minimal-MCP, handing it the brief, and
// getting the CEO's pass/fail — is injected as `executeStep`, so the sequencing here (advance / rework / bound /
// settle) is a pure, testable loop and the session integration is a thin adapter around it.
internal sealed class AutopilotRunDriver(
    AutopilotPlanController controller,
    int maxAttempts,
    Func<AutopilotStep, Task<AutopilotStep>>? holdToCostCeiling = null)
{
    // Runs the plan: while the run is `AutopilotPlanPhase.Running` and a step is still pending, it starts
    // the step (recording the attempt), executes it, validates the outcome, and lets a rework re-run the same step
    // until it passes or its attempts run out. It stops early if the run leaves Running (a blockade parks it), and
    // settles once no step is pending. `executeStep` returns whether a verdict was ever reached and,
    // if so, what it was (`AutopilotStepOutcome`) — `AutopilotStepOutcome.Rejected` (the CEO
    // judged and turned it down) and `AutopilotStepOutcome.Faulted` (no verdict at all: a crash, a stall,
    // a refused session) both rework or bound the same way, but only the former is ever a review finding.
    //
    // AC-434: when the next pending work is a review-gate pair (`AutopilotPlan.NextPendingGroup` returns
    // more than one step), `_RunReviewGroupAsync` runs them concurrently instead of one at a time.
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

            // AC-434: a single pending review-gate step (the CEO's other gate already settled, or the operator dropped
            // one) still runs through _RunReviewGroupAsync rather than the plain single-step path below — a lone gate's
            // rejection still needs the shared fix step, not a rework that fixes nothing (its own worktree is a
            // throwaway fork, never the run's real one; see AutopilotRunCoordinator).
            if (group.Count == 1 && !group[0].IsReviewGate)
            {
                await _DriveStepToSettledAsync(group[0], executeStep, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await _RunReviewGroupAsync(group, executeStep, cancellationToken).ConfigureAwait(false);
        }
    }

    // Runs one step through start/execute/validate, reworking it in place while ValidateStep says to, until it settles
    // (passed, or gave up after the last attempt) or the run leaves Running. Extracted so a review group's shared fix
    // pass (AC-434) drives its synthesized step through the identical cycle an ordinary plan step gets — this is the
    // original single-step loop body, unchanged, just given a name so both callers share it.
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

    // AC-434: read-parallel, write-serial. Every gate still open this round runs concurrently (each in its own
    // throwaway worktree — AutopilotRunCoordinator never lets a review-gate step touch the run's shared one), keeping
    // its own verdict — the coordinator queues their CEO validations one at a time internally, so this stays a plain
    // concurrent execute+validate from here. A gate that comes back clean settles immediately and never reruns for the
    // other's sake. When at least one gate is genuinely REJECTED (the CEO judged it, AC-347) with attempts left, one
    // shared fix step — the run's only writer — applies every rejected gate's finding before it re-verifies. A gate
    // that only FAULTED (no verdict at all — a crash, a stall) is not a finding and contributes nothing to the fix
    // brief; it is simply retried next round like an ordinary rework. Bounded by the ordinary attempt cap on both the
    // gates and the fix pass, so this can never loop forever.
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

            // AC-256: this step inherits the gate's profile and model, and a review gate is exempt from the run's cost
            // ceiling — so an expensive model legitimately assigned to a reviewer would otherwise carry straight into
            // the one step that actually writes code, without ever passing the gate that plan emission applies. Null in
            // a bare test graph, where there is no roster to hold anything to.
            if (holdToCostCeiling is not null)
            {
                try
                {
                    fixStep = await holdToCostCeiling(fixStep).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Reading the profile roster is the only thing that can fault in there, and it is the same
                    // best-effort treatment the coordinator's own roster reads get. A run that dies silently because a
                    // config file was briefly unreadable is a far worse outcome than one fix pass costing a tier more
                    // than the ceiling wanted.
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
