namespace Cockpit.Plugin.Autopilot;

// Runs approved plans from the queue, up to `AutopilotSettings.MaxConcurrentRuns` at once (AC-174). A tool call is
// routed to whichever running run owns the caller's pane — every coordinator self-gates, so trying each is safe.
// `Runner` is set by the workspace body while it is open, so this is testable without live sessions via a fake one.
internal sealed class AutopilotRunManager(AutopilotRunQueue queue, AutopilotSettings settings)
{
    private readonly Lock _lock = new();
    private readonly List<AutopilotRunCoordinator> _active = [];
    private Func<AutopilotPlan, AutopilotRunHandle>? _runner;

    // Raised when a run starts or ends, or the queue changes — the surface re-renders and the pump re-checks capacity.
    public event Action? Changed;

    // Starts a run for a dequeued plan and hands back its coordinator and completion task. Set by the workspace body
    // while it is open (a run embeds sessions in the workspace's context) and cleared when it closes; setting it starts
    // any runs that were waiting for a runner. Null means no run can start yet, so plans wait in the queue.
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

    // The coordinators of the runs executing now — how the surface finds each running run to render it.
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

    // Adds an approved plan and starts it if there is a free slot, else it waits in the queue in order.
    public void Submit(AutopilotPlan plan)
    {
        queue.Enqueue(plan);
        Changed?.Invoke();
        _Pump();
    }

    // A step agent reported done — hand it to the run that owns its pane. False when no running run does.
    public bool ReportStepDone(string paneId, string summary) => _Route(coordinator => coordinator.ReportStepDone(paneId, summary));

    // A CEO reported its validation verdict — hand it to the run whose CEO pane it is.
    public bool ReportValidation(string paneId, bool passed, string? reason) => _Route(coordinator => coordinator.ReportValidation(paneId, passed, reason));

    // A step worker consults its manager (AC-201) — routed to the run that owns the worker's pane.
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

    // The CEO answers a worker's consult (AC-201) — routed to the run whose CEO pane it is.
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

    // The CEO escalates a worker's consult to the operator (AC-201) — routed to the run whose CEO pane it is.
    public bool EscalateToOperator(string paneId, string question) => _Route(coordinator => coordinator.EscalateToOperator(paneId, question));

    // The CEO moves its source issue's tracker stage — routed to the run whose CEO pane it is.
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

    // The CEO posts evidence on its source issue — routed to the run whose CEO pane it is.
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
