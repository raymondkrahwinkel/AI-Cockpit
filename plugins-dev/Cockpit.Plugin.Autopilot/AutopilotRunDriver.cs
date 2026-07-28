namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// Drives an approved plan to completion (AC-174) — the bounded agentic loop: pick the next pending
/// step, run it, have the CEO validate its output against the step's acceptance, and either advance, send it back to
/// rework (re-run), or give up once the attempt cap is hit — then settle the run merge-ready or blocked.
/// <para>
/// The actual work of a step — embedding a session on the step's profile/model/minimal-MCP, handing it the brief, and
/// getting the CEO's pass/fail — is injected as <c>executeStep</c>, so the sequencing here (advance / rework / bound /
/// settle) is a pure, testable loop and the session integration is a thin adapter around it.
/// </para>
/// </summary>
internal sealed class AutopilotRunDriver(AutopilotPlanController controller, int maxAttempts)
{
    /// <summary>
    /// Runs the plan: while the run is <see cref="AutopilotPlanPhase.Running"/> and a step is still pending, it starts
    /// the step (recording the attempt), executes it, validates the outcome, and lets a rework re-run the same step
    /// until it passes or its attempts run out. It stops early if the run leaves Running (a blockade parks it), and
    /// settles once no step is pending. <paramref name="executeStep"/> returns whether a verdict was ever reached and,
    /// if so, what it was (<see cref="AutopilotStepOutcome"/>) — <see cref="AutopilotStepOutcome.Rejected"/> (the CEO
    /// judged and turned it down) and <see cref="AutopilotStepOutcome.Faulted"/> (no verdict at all: a crash, a stall,
    /// a refused session) both rework or bound the same way, but only the former is ever a review finding.
    /// </summary>
    public async Task RunAsync(Func<AutopilotStep, Task<AutopilotStepOutcome>> executeStep, CancellationToken cancellationToken = default)
    {
        while (controller.Phase == AutopilotPlanPhase.Running && !cancellationToken.IsCancellationRequested)
        {
            if (controller.Plan?.NextPending is not { } next)
            {
                controller.Settle();
                return;
            }

            controller.StartStep(next.Id);

            // The current step object carries the attempt just recorded and this run's profile/model/MCP.
            var step = controller.Plan?.Steps.FirstOrDefault(candidate => candidate.Id == next.Id) ?? next;

            AutopilotStepOutcome outcome;
            try
            {
                outcome = await executeStep(step).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A step whose execution threw is a failed attempt, not a crashed run — it reworks or gives up like any
                // other failure, so a flaky session or a broken profile cannot take the whole run down. It never saw a
                // verdict, so it is Faulted, not Rejected — a thrown exception is not the CEO turning the step down.
                outcome = AutopilotStepOutcome.Faulted;
            }

            // A rework leaves the step Pending, so the next loop picks it up again (StartStep records the next attempt);
            // a settled step (passed, or failed out of attempts) means NextPending moves on — or, when none is left, settle.
            controller.ValidateStep(next.Id, outcome, maxAttempts);
        }
    }
}
