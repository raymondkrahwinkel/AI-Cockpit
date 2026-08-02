namespace Cockpit.Plugin.Autopilot;

// The single place a CEO-planned run's state lives (AC-174): the living plan, the phase, and the blockade state. It
// mirrors `AutopilotRunController`'s shape — a `Changed` event and thread-guarded state — so
// the workspace body tracks it the same way, and the plan-based flow can grow up alongside the shipped gate-based one
// rather than replacing it in one move.
//
// The planning round edits the plan freely through `UpdatePlan` (the CEO re-emits it, the operator tweaks a
// step); `Approve` freezes it and starts the run; `StartStep`/`SettleStep` drive the
// steps; `Block`/`ResumeRunning`/`Park` handle the blockade; and `Settle`
// ends the run merge-ready or blocked by the per-step hard/skip policy.
//
// Every mutable field is read and written under `_lock`: the plan and the phase are touched from the CEO's
// report tools on background MCP-call threads, the driver loop, and the UI thread at once. `Changed` is
// always raised outside the lock so a re-entrant render cannot deadlock or run while the state is half-updated.
internal sealed class AutopilotPlanController
{
    private readonly Lock _lock = new();
    private AutopilotPlan? _plan;
    private AutopilotPlanPhase _phase;
    private string? _blockReason;
    private string? _pendingQuestion;
    private string? _sessionPaneId;
    private int _blockadeAnswers;
    private bool _pullRequestMissing;

    // The current plan, or null before a planning round has begun.
    public AutopilotPlan? Plan
    {
        get
        {
            lock (_lock)
            {
                return _plan;
            }
        }
    }

    // Where the run sits. Read under the lock, since it is written from MCP-call threads and the driver loop.
    public AutopilotPlanPhase Phase
    {
        get
        {
            lock (_lock)
            {
                return _phase;
            }
        }
    }

    // Why the run parked, when `Phase` is `AutopilotPlanPhase.Blocked`; otherwise null.
    public string? BlockReason
    {
        get
        {
            lock (_lock)
            {
                return _blockReason;
            }
        }
    }

    // The question a step is blocked on (AC-155), when `Phase` is AwaitingOperator; otherwise null.
    public string? PendingQuestion
    {
        get
        {
            lock (_lock)
            {
                return _pendingQuestion;
            }
        }
    }

    // The pane id of the step's embedded session, once the body has embedded it — how a report is bound to this run.
    public string? SessionPaneId
    {
        get
        {
            lock (_lock)
            {
                return _sessionPaneId;
            }
        }
    }

    // How many blockade questions the operator has answered this run (AC-347) — counted, and explicitly not a
    // correction (see `RecordBlockadeAnswer`).
    public int BlockadeAnswers
    {
        get
        {
            lock (_lock)
            {
                return _blockadeAnswers;
            }
        }
    }

    // Whether this run reached merge-ready but could not deliver its pull request (AC-347) — a run that leaves
    // the surface still needing a human to open its PR by hand is not "settled clean", even though every hard step
    // passed. Read under `_lock`, like the other properties.
    public bool PullRequestMissing
    {
        get
        {
            lock (_lock)
            {
                return _pullRequestMissing;
            }
        }
    }

    public event EventHandler? Changed;

    // The step running now, or null when none is — a shortcut over the plan for the surface and the driver.
    public AutopilotStep? ActiveStep => Plan?.Active;

    // Whether every step has settled (nothing left Pending or Running) — the signal to `Settle` the run.
    public bool AllSettled =>
        Plan is { } plan &&
        plan.Steps.Count > 0 &&
        plan.Steps.All(step => step.Status is not (AutopilotStepStatus.Pending or AutopilotStepStatus.Running));

    // Opens the planning round on `plan` (typically an empty or freshly drafted plan). Refuses (returns
    // false, leaving the existing run untouched) while a run is live — `AutopilotPlanPhase.Running` or
    // `AutopilotPlanPhase.AwaitingOperator` — so a second trigger cannot overwrite a run in flight and strand
    // its agents. A settled run (merge-ready/blocked) or an idle controller starts fresh.
    public bool BeginPlanning(AutopilotPlan plan)
    {
        lock (_lock)
        {
            if (_phase is AutopilotPlanPhase.Running or AutopilotPlanPhase.AwaitingOperator)
            {
                return false;
            }

            _plan = plan;
            _phase = AutopilotPlanPhase.Planning;
            _blockReason = null;
            _pendingQuestion = null;
            _sessionPaneId = null;
            _blockadeAnswers = 0;
            _pullRequestMissing = false;
        }

        _Raise();
        return true;
    }

    // Replaces the plan while it is still being shaped — the CEO re-emitting it, or an operator edit. Planning only.
    public void UpdatePlan(AutopilotPlan plan)
    {
        lock (_lock)
        {
            _plan = plan;
        }

        _Raise();
    }

    // The operator dismissed the planning round without approving — clears the draft so the surface returns to
    // its empty state and the pop-out does not reopen. Planning only; a live or settled run is left untouched.
    public void CancelPlanning()
    {
        lock (_lock)
        {
            if (_phase != AutopilotPlanPhase.Planning)
            {
                return;
            }

            _plan = null;
        }

        _Raise();
    }

    // Freezes the plan and starts the autonomous run — the single approval gate. Refuses an empty plan (nothing to run)
    // or an approval outside the planning round, returning false so the caller can keep shaping it.
    public bool Approve()
    {
        lock (_lock)
        {
            if (_phase != AutopilotPlanPhase.Planning || _plan is not { Steps.Count: > 0 })
            {
                return false;
            }

            _phase = AutopilotPlanPhase.Running;
            _blockReason = null;
        }

        _Raise();
        return true;
    }

    // Marks the step with `stepId` as running and records the (re-)run attempt — the driver
    // starting the next step's session. The attempt count bounds the rework loop in `ValidateStep`.
    public void StartStep(string stepId) =>
        _MutateStep(stepId, step => step.WithAttempt().WithStatus(AutopilotStepStatus.Running));

    // Records what a step's execution actually produced (AC-347): `AutopilotStepOutcome.Passed` settles the
    // step; `AutopilotStepOutcome.Rejected` — the CEO judged the output against its acceptance and turned
    // it down — or `AutopilotStepOutcome.Faulted` — no verdict was ever reached (a crash, a stall, a
    // refused session, a dead CEO) — both send it back to rework (`AutopilotStepStatus.Pending`) while it
    // still has attempts left under `maxAttempts`, and settle it
    // `AutopilotStepStatus.Failed` once those run out — so a rework loop is bounded and never becomes an
    // endless loop. Only a `AutopilotStepOutcome.Rejected` rework counts as a rework
    // (`AutopilotStep.WithRework`): a `AutopilotStepOutcome.Faulted` restart is a run restart,
    // never a review finding, because nobody judged the work — this is the one place that distinction is recorded, the
    // distinction a plain `bool` could not carry. Returns true when the step goes back to rework (the driver
    // re-runs it), false when it settled (passed, or gave up after the last attempt).
    public bool ValidateStep(string stepId, AutopilotStepOutcome outcome, int maxAttempts)
    {
        AutopilotStep? step;
        lock (_lock)
        {
            step = _plan?.Steps.FirstOrDefault(candidate => candidate.Id == stepId);
        }

        if (step is null)
        {
            return false;
        }

        if (outcome == AutopilotStepOutcome.Passed)
        {
            _SetStepStatus(stepId, AutopilotStepStatus.Passed);
            return false;
        }

        if (step.Attempts >= maxAttempts)
        {
            _SetStepStatus(stepId, AutopilotStepStatus.Failed);
            return false;
        }

        // Attempts left: back to rework — the driver re-runs the step, and StartStep records the next attempt. Only a
        // Rejected outcome (an actual CEO verdict) counts as a rework; a Faulted one (no verdict at all) moves the step
        // back to Pending too, but without WithRework() — it is a run restart, not a review finding. Each branch is one
        // mutation that records the status and (for Rejected) the rework together, so a re-entrant read never sees the
        // rework count bumped without the status having moved (or vice versa).
        if (outcome == AutopilotStepOutcome.Rejected)
        {
            _MutateStep(stepId, target => target.WithRework().WithStatus(AutopilotStepStatus.Pending));
        }
        else
        {
            _SetStepStatus(stepId, AutopilotStepStatus.Pending);
        }

        return true;
    }

    // Records how a step finished (passed/failed/skipped/blocked); `Settle` reads it when all are in.
    public void SettleStep(string stepId, AutopilotStepStatus outcome)
    {
        if (outcome is AutopilotStepStatus.Pending or AutopilotStepStatus.Running)
        {
            return;
        }

        _SetStepStatus(stepId, outcome);
    }

    // Settles the run once every step is in: merge-ready when every `GateMode.Hard` step passed, else blocked,
    // naming the hard steps that did not. Skippable steps that were skipped or failed are a warning on the item, not a stop.
    public void Settle()
    {
        lock (_lock)
        {
            var unmet = _plan is { } plan
                ? plan.Steps
                    .Where(step => step.Mode == GateMode.Hard && step.Status != AutopilotStepStatus.Passed)
                    .Select(step => step.Title)
                    .ToList()
                : [];

            if (unmet.Count > 0)
            {
                _phase = AutopilotPlanPhase.Blocked;
                _blockReason = $"Required step(s) did not pass: {string.Join(", ", unmet)}.";
            }
            else
            {
                _phase = AutopilotPlanPhase.MergeReady;
                _blockReason = null;
            }
        }

        _Raise();
    }

    // A step hit a blockade and needs the operator (AC-155): the run waits, showing `question`.
    public void Block(string question)
    {
        lock (_lock)
        {
            _phase = AutopilotPlanPhase.AwaitingOperator;
            _pendingQuestion = question;
        }

        _Raise();
    }

    // The blockade cleared (the operator answered): the run goes back to running.
    public void ResumeRunning()
    {
        lock (_lock)
        {
            _phase = AutopilotPlanPhase.Running;
            _pendingQuestion = null;
        }

        _Raise();
    }

    // Parks the run with `reason` — e.g. a blockade unanswered within the grace time.
    public void Park(string reason)
    {
        lock (_lock)
        {
            _phase = AutopilotPlanPhase.Blocked;
            _blockReason = reason;
            _pendingQuestion = null;
        }

        _Raise();
    }

    // The operator stopped the run mid-flight (AC-196): settle it `AutopilotPlanPhase.Stopped` with
    // `reason` so it is recorded as a deliberate stop rather than vanishing while still Running. Set
    // before the run's cancellation tears the driver down, so the settled phase the surface snapshots is Stopped — the
    // driver only calls `Settle` when every step finished, which a mid-run stop never reaches.
    public void Stop(string reason)
    {
        lock (_lock)
        {
            _phase = AutopilotPlanPhase.Stopped;
            _blockReason = reason;
            _pendingQuestion = null;
        }

        _Raise();
    }

    // Binds a step's embedded session pane so a report from that pane is trusted as this run's. Does not raise
    // `Changed` — it changes no visible state, and firing mid-embed would re-enter the body's render.
    public void BindSession(string paneId)
    {
        lock (_lock)
        {
            _sessionPaneId = paneId;
        }
    }

    // Counts a blockade question the operator answered (AC-347) — a blockade the run itself raised and the operator
    // resolved is explicitly *not* a correction, so it is tracked as its own figure rather than folded into
    // `AutopilotCorrectionKind`. Called only from `AutopilotRunCoordinator.AnswerBlockadeAsync` —
    // the one place an operator answers a blockade. Deliberately not raised from inside `ResumeRunning`
    // itself: that would make any future second caller of `ResumeRunning` silently count too, whether or not
    // it was actually an operator answering. Does not raise `Changed` — `ResumeRunning` already
    // does, right after this is called.
    public void RecordBlockadeAnswer()
    {
        lock (_lock)
        {
            _blockadeAnswers++;
        }
    }

    // Marks that this merge-ready run could not deliver its pull request (AC-347) — no `gh`, no remote, or
    // `PublishAsync` itself failed. Called only from `AutopilotRunCoordinator._FinalizeMergeReadyAsync`,
    // the one place delivery is decided. Does not raise `Changed`: it changes no visible run state (the
    // phase stays MergeReady, the operator already sees the finalization's own toast/note) — it is read later, from
    // history, once the run has settled.
    public void RecordPullRequestMissing()
    {
        lock (_lock)
        {
            _pullRequestMissing = true;
        }
    }

    // Records a note on a step's latest outcome (AC-174) — why it failed, or a progress line — so the pipeline block can show it. A blank note just clears it.
    public void NoteStep(string stepId, string note) =>
        _MutateStep(stepId, step => step.WithNote(note));

    // Appends a new step to the running plan (AC-434) — how a review group's shared fix pass joins the
    // pipeline the operator already sees, without the CEO having planned it up front. A no-op before a plan exists.
    public void InsertStep(AutopilotStep step)
    {
        lock (_lock)
        {
            if (_plan is not { } plan)
            {
                return;
            }

            _plan = plan.WithSteps([.. plan.Steps, step]);
        }

        _Raise();
    }

    private void _SetStepStatus(string stepId, AutopilotStepStatus status) =>
        _MutateStep(stepId, step => step.WithStatus(status));

    private void _MutateStep(string stepId, Func<AutopilotStep, AutopilotStep> mutate)
    {
        lock (_lock)
        {
            if (_plan is not { } plan || plan.Steps.FirstOrDefault(step => step.Id == stepId) is not { } target)
            {
                return;
            }

            _plan = plan.WithStep(mutate(target));
        }

        _Raise();
    }

    private void _Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
