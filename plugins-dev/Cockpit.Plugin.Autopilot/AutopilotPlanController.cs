namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The single place a CEO-planned run's state lives (AC-174): the living plan, the phase, and the blockade state. It
/// mirrors <see cref="AutopilotRunController"/>'s shape — a <see cref="Changed"/> event and thread-guarded state — so
/// the workspace body tracks it the same way, and the plan-based flow can grow up alongside the shipped gate-based one
/// rather than replacing it in one move.
/// <para>
/// The planning round edits the plan freely through <see cref="UpdatePlan"/> (the CEO re-emits it, the operator tweaks a
/// step); <see cref="Approve"/> freezes it and starts the run; <see cref="StartStep"/>/<see cref="SettleStep"/> drive the
/// steps; <see cref="Block"/>/<see cref="ResumeRunning"/>/<see cref="Park"/> handle the blockade; and <see cref="Settle"/>
/// ends the run merge-ready or blocked by the per-step hard/skip policy.
/// </para>
/// <para>
/// Every mutable field is read and written under <see cref="_lock"/>: the plan and the phase are touched from the CEO's
/// report tools on background MCP-call threads, the driver loop, and the UI thread at once. <see cref="Changed"/> is
/// always raised outside the lock so a re-entrant render cannot deadlock or run while the state is half-updated.
/// </para>
/// </summary>
internal sealed class AutopilotPlanController
{
    private readonly Lock _lock = new();
    private AutopilotPlan? _plan;
    private AutopilotPlanPhase _phase;
    private string? _blockReason;
    private string? _pendingQuestion;
    private string? _sessionPaneId;

    /// <summary>The current plan, or null before a planning round has begun.</summary>
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

    /// <summary>Where the run sits. Read under the lock, since it is written from MCP-call threads and the driver loop.</summary>
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

    /// <summary>Why the run parked, when <see cref="Phase"/> is <see cref="AutopilotPlanPhase.Blocked"/>; otherwise null.</summary>
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

    /// <summary>The question a step is blocked on (AC-155), when <see cref="Phase"/> is AwaitingOperator; otherwise null.</summary>
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

    /// <summary>The pane id of the step's embedded session, once the body has embedded it — how a report is bound to this run.</summary>
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

    public event EventHandler? Changed;

    /// <summary>The step running now, or null when none is — a shortcut over the plan for the surface and the driver.</summary>
    public AutopilotStep? ActiveStep => Plan?.Active;

    /// <summary>Whether every step has settled (nothing left Pending or Running) — the signal to <see cref="Settle"/> the run.</summary>
    public bool AllSettled =>
        Plan is { } plan &&
        plan.Steps.Count > 0 &&
        plan.Steps.All(step => step.Status is not (AutopilotStepStatus.Pending or AutopilotStepStatus.Running));

    /// <summary>
    /// Opens the planning round on <paramref name="plan"/> (typically an empty or freshly drafted plan). Refuses (returns
    /// false, leaving the existing run untouched) while a run is live — <see cref="AutopilotPlanPhase.Running"/> or
    /// <see cref="AutopilotPlanPhase.AwaitingOperator"/> — so a second trigger cannot overwrite a run in flight and strand
    /// its agents. A settled run (merge-ready/blocked) or an idle controller starts fresh.
    /// </summary>
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
        }

        _Raise();
        return true;
    }

    /// <summary>Replaces the plan while it is still being shaped — the CEO re-emitting it, or an operator edit. Planning only.</summary>
    public void UpdatePlan(AutopilotPlan plan)
    {
        lock (_lock)
        {
            _plan = plan;
        }

        _Raise();
    }

    /// <summary>The operator dismissed the planning round without approving — clears the draft so the surface returns to
    /// its empty state and the pop-out does not reopen. Planning only; a live or settled run is left untouched.</summary>
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

    /// <summary>
    /// Freezes the plan and starts the autonomous run — the single approval gate. Refuses an empty plan (nothing to run)
    /// or an approval outside the planning round, returning false so the caller can keep shaping it.
    /// </summary>
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

    /// <summary>Marks the step with <paramref name="stepId"/> as running and records the (re-)run attempt — the driver
    /// starting the next step's session. The attempt count bounds the rework loop in <see cref="ValidateStep"/>.</summary>
    public void StartStep(string stepId) =>
        _MutateStep(stepId, step => step.WithAttempt().WithStatus(AutopilotStepStatus.Running));

    /// <summary>
    /// Records the CEO's validation of a step's output against its acceptance (AC-174, Raymond 2026-07-21). A pass settles
    /// the step; a fail sends it back to rework (<see cref="AutopilotStepStatus.Pending"/>) while it still has attempts
    /// left under <paramref name="maxAttempts"/>, and settles it <see cref="AutopilotStepStatus.Failed"/> once those run
    /// out — so a rework loop is bounded and never becomes an endless loop. Returns true when the step goes back to
    /// rework (the driver re-runs it), false when it settled (passed, or gave up after the last attempt).
    /// </summary>
    public bool ValidateStep(string stepId, bool passed, int maxAttempts)
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

        if (passed)
        {
            _SetStepStatus(stepId, AutopilotStepStatus.Passed);
            return false;
        }

        if (step.Attempts >= maxAttempts)
        {
            _SetStepStatus(stepId, AutopilotStepStatus.Failed);
            return false;
        }

        // Attempts left: back to rework — the driver re-runs the step, and StartStep records the next attempt.
        _SetStepStatus(stepId, AutopilotStepStatus.Pending);
        return true;
    }

    /// <summary>Records how a step finished (passed/failed/skipped/blocked); <see cref="Settle"/> reads it when all are in.</summary>
    public void SettleStep(string stepId, AutopilotStepStatus outcome)
    {
        if (outcome is AutopilotStepStatus.Pending or AutopilotStepStatus.Running)
        {
            return;
        }

        _SetStepStatus(stepId, outcome);
    }

    /// <summary>
    /// Settles the run once every step is in: merge-ready when every <see cref="GateMode.Hard"/> step passed, else blocked,
    /// naming the hard steps that did not. Skippable steps that were skipped or failed are a warning on the item, not a stop.
    /// </summary>
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

    /// <summary>A step hit a blockade and needs the operator (AC-155): the run waits, showing <paramref name="question"/>.</summary>
    public void Block(string question)
    {
        lock (_lock)
        {
            _phase = AutopilotPlanPhase.AwaitingOperator;
            _pendingQuestion = question;
        }

        _Raise();
    }

    /// <summary>The blockade cleared (the operator answered): the run goes back to running.</summary>
    public void ResumeRunning()
    {
        lock (_lock)
        {
            _phase = AutopilotPlanPhase.Running;
            _pendingQuestion = null;
        }

        _Raise();
    }

    /// <summary>Parks the run with <paramref name="reason"/> — e.g. a blockade unanswered within the grace time.</summary>
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

    /// <summary>Binds a step's embedded session pane so a report from that pane is trusted as this run's. Does not raise
    /// <see cref="Changed"/> — it changes no visible state, and firing mid-embed would re-enter the body's render.</summary>
    public void BindSession(string paneId)
    {
        lock (_lock)
        {
            _sessionPaneId = paneId;
        }
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
