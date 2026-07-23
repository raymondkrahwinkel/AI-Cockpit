namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// Runs approved plans from the queue, up to <see cref="AutopilotSettings.MaxConcurrentRuns"/> at once (AC-174, Raymond):
/// an approved plan is submitted here, executes now if there is a free slot, else waits in the <see cref="AutopilotRunQueue"/>
/// until one frees. Each running run gets its own coordinator; a tool call (a step reporting done, the CEO validating)
/// is routed to whichever running run owns the caller's pane — every coordinator self-gates on its own panes, so trying
/// each is safe. Starting a run is the <see cref="Runner"/> the workspace body sets while it is open (a run needs the
/// workspace's context to embed sessions), so the concurrency logic here is testable without live sessions by setting a
/// fake runner. A run submitted with no runner set (the workspace closed) simply waits in the queue until one is.
/// </summary>
internal sealed class AutopilotRunManager(AutopilotRunQueue queue, AutopilotSettings settings)
{
    private readonly Lock _lock = new();
    private readonly List<AutopilotRunCoordinator> _active = [];
    private Func<AutopilotPlan, AutopilotRunHandle>? _runner;

    /// <summary>Raised when a run starts or ends, or the queue changes — the surface re-renders and the pump re-checks capacity.</summary>
    public event Action? Changed;

    /// <summary>
    /// Starts a run for a dequeued plan and hands back its coordinator and completion task. Set by the workspace body
    /// while it is open (a run embeds sessions in the workspace's context) and cleared when it closes; setting it starts
    /// any runs that were waiting for a runner. Null means no run can start yet, so plans wait in the queue.
    /// </summary>
    public Func<AutopilotPlan, AutopilotRunHandle>? Runner
    {
        get => _runner;
        set
        {
            _runner = value;
            if (value is not null)
            {
                _Pump();
            }
        }
    }

    /// <summary>The coordinators of the runs executing now — how the surface finds each running run to render it.</summary>
    public IReadOnlyList<AutopilotRunCoordinator> Active
    {
        get
        {
            lock (_lock)
            {
                return [.. _active];
            }
        }
    }

    /// <summary>Adds an approved plan and starts it if there is a free slot, else it waits in the queue in order.</summary>
    public void Submit(AutopilotPlan plan)
    {
        queue.Enqueue(plan);
        Changed?.Invoke();
        _Pump();
    }

    /// <summary>A step agent reported done — hand it to the run that owns its pane. False when no running run does.</summary>
    public bool ReportStepDone(string paneId, string summary) => _Route(coordinator => coordinator.ReportStepDone(paneId, summary));

    /// <summary>A CEO reported its validation verdict — hand it to the run whose CEO pane it is.</summary>
    public bool ReportValidation(string paneId, bool passed, string? reason) => _Route(coordinator => coordinator.ReportValidation(paneId, passed, reason));

    /// <summary>A step worker consults its manager (AC-201) — routed to the run that owns the worker's pane.</summary>
    public async Task<bool> ReportConsultAsync(string paneId, string question)
    {
        foreach (var coordinator in Active)
        {
            if (await coordinator.ReportConsultAsync(paneId, question))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The CEO answers a worker's consult (AC-201) — routed to the run whose CEO pane it is.</summary>
    public async Task<bool> AnswerWorkerAsync(string paneId, string answer)
    {
        foreach (var coordinator in Active)
        {
            if (await coordinator.AnswerWorkerAsync(paneId, answer))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The CEO escalates a worker's consult to the operator (AC-201) — routed to the run whose CEO pane it is.</summary>
    public bool EscalateToOperator(string paneId, string question) => _Route(coordinator => coordinator.EscalateToOperator(paneId, question));

    /// <summary>The CEO moves its source issue's tracker stage — routed to the run whose CEO pane it is.</summary>
    public async Task<bool> ReportTrackerStageAsync(string paneId, string stage)
    {
        foreach (var coordinator in Active)
        {
            if (await coordinator.ReportTrackerStageAsync(paneId, stage))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The CEO posts evidence on its source issue — routed to the run whose CEO pane it is.</summary>
    public async Task<bool> ReportTrackerNoteAsync(string paneId, string note)
    {
        foreach (var coordinator in Active)
        {
            if (await coordinator.ReportTrackerNoteAsync(paneId, note))
            {
                return true;
            }
        }

        return false;
    }

    // Starts as many queued runs as there is capacity for, then returns. Called on submit and whenever a run frees a
    // slot. A reservation counter keeps the capacity check honest while a start runs outside the lock (starting a run
    // embeds a session, which must not happen while the lock is held).
    private void _Pump()
    {
        while (true)
        {
            var runner = _runner;
            if (runner is null)
            {
                return;
            }

            AutopilotPlan? next;
            lock (_lock)
            {
                if (_active.Count + _starting >= settings.MaxConcurrentRuns() || !queue.TryDequeue(out next))
                {
                    return;
                }

                _starting++;
            }

            // The reservation must be released whatever the runner does: if it throws (a session failed to embed), a
            // finally-less path would leak the slot forever — _starting never decremented — and the queue would start
            // fewer and fewer runs. Release it in finally; only a run that actually started is added to _active.
            AutopilotRunHandle? handle = null;
            try
            {
                handle = runner(next!);
            }
            finally
            {
                lock (_lock)
                {
                    _starting--;
                    if (handle is not null)
                    {
                        _active.Add(handle.Coordinator);
                    }
                }
            }

            // Reached only when the runner returned a handle (a throw is rethrown out of the finally above).
            Changed?.Invoke();
            _ = _ReleaseWhenDoneAsync(handle!);
        }
    }

    private int _starting;

    private async Task _ReleaseWhenDoneAsync(AutopilotRunHandle handle)
    {
        try
        {
            await handle.Completed;
        }
        catch (Exception)
        {
            // A run's own failure must not stall the queue — it settled or died; either way the slot is now free.
        }

        lock (_lock)
        {
            _active.Remove(handle.Coordinator);
        }

        Changed?.Invoke();
        _Pump();
    }

    private bool _Route(Func<AutopilotRunCoordinator, bool> call)
    {
        foreach (var coordinator in Active)
        {
            if (call(coordinator))
            {
                return true;
            }
        }

        return false;
    }
}
