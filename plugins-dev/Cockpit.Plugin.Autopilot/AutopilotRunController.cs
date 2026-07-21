namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The single place a run's state lives between the trigger, the scoping judgment, the session and the done-gate
/// (AC-150–AC-153). The "start" intent handler drives it through <see cref="BeginScoping"/> → <see cref="Refuse"/> or
/// <see cref="MarkRunning"/>; the workspace body binds the embedded session's pane; the agent reports gate outcomes
/// through the in-process endpoint, and <see cref="MarkReady"/> settles the run to merge-ready or blocked by the
/// per-gate hard/skip policy. The body reads <see cref="Current"/>/<see cref="Phase"/>/<see cref="Gates"/> and re-reads
/// them on <see cref="Changed"/>. One controller is shared by the handler, the body and the report endpoint.
/// </summary>
internal sealed class AutopilotRunController(AutopilotSettings settings)
{
    // Gate reports arrive on a background MCP-call thread while the UI thread reads them for the status strip, so guard
    // the maps — a plain Dictionary mutated during another thread's read throws.
    private readonly Lock _gateLock = new();
    private readonly Dictionary<GateKind, AutopilotGateOutcome> _gates = new();
    private readonly Dictionary<GateKind, string?> _evidence = new();

    public AutopilotRun? Current { get; private set; }

    public AutopilotRunPhase Phase { get; private set; }

    /// <summary>Why the current run was refused or blocked, when <see cref="Phase"/> is Refused/Blocked; otherwise null.</summary>
    public string? BlockReason { get; private set; }

    /// <summary>The pane id of the run's embedded session, once the body has embedded it — how a gate report is bound to this run.</summary>
    public string? SessionPaneId { get; private set; }

    /// <summary>The merge-ready pull request the agent opened and reported, or null — posted back to the tracker as evidence.</summary>
    public string? PrUrl { get; private set; }

    /// <summary>The question the agent is blocked on and waiting for the operator to answer (AC-155), when <see cref="Phase"/> is AwaitingOperator; otherwise null.</summary>
    public string? PendingQuestion { get; private set; }

    /// <summary>A snapshot of the gate outcomes the agent has reported for the current run so far.</summary>
    public IReadOnlyDictionary<GateKind, AutopilotGateOutcome> Gates
    {
        get
        {
            lock (_gateLock)
            {
                return new Dictionary<GateKind, AutopilotGateOutcome>(_gates);
            }
        }
    }

    /// <summary>The note or link the agent attached to a reported gate, or null — shown against the gate in the status strip.</summary>
    public string? GateEvidence(GateKind gate)
    {
        lock (_gateLock)
        {
            return _evidence.GetValueOrDefault(gate);
        }
    }

    public event EventHandler? Changed;

    /// <summary>Whether <paramref name="run"/> is still the current run — false once a newer point replaced it.</summary>
    public bool IsCurrent(AutopilotRun run) => ReferenceEquals(Current, run);

    /// <summary>A new point arrived: it becomes the current run, being scoped, replacing any earlier one and its gates.</summary>
    public void BeginScoping(AutopilotRun run)
    {
        Current = run;
        Phase = AutopilotRunPhase.Scoping;
        BlockReason = null;
        SessionPaneId = null;
        PrUrl = null;
        PendingQuestion = null;
        lock (_gateLock)
        {
            _gates.Clear();
            _evidence.Clear();
        }

        _Raise();
    }

    /// <summary>Scoping refused the point (or the operator declined it): it parks with <paramref name="reason"/>.</summary>
    public void Refuse(string reason)
    {
        Phase = AutopilotRunPhase.Refused;
        BlockReason = reason;
        _Raise();
    }

    /// <summary>Scoping passed and the operator approved: the run advances to running, the signal the body embeds its session on.</summary>
    public void MarkRunning()
    {
        Phase = AutopilotRunPhase.Running;
        BlockReason = null;
        _Raise();
    }

    /// <summary>The agent hit a blockade and needs the operator (AC-155): the run waits, showing <paramref name="question"/>, until the operator answers on the issue or the grace timer runs out.</summary>
    public void Block(string question)
    {
        Phase = AutopilotRunPhase.AwaitingOperator;
        PendingQuestion = question;
        _Raise();
    }

    /// <summary>The blockade cleared (the operator answered): the run goes back to running.</summary>
    public void ResumeRunning()
    {
        Phase = AutopilotRunPhase.Running;
        PendingQuestion = null;
        _Raise();
    }

    /// <summary>Parks the run as blocked with <paramref name="reason"/> — e.g. the operator did not answer a blockade in the grace time (AC-155).</summary>
    public void Park(string reason)
    {
        Phase = AutopilotRunPhase.Blocked;
        BlockReason = reason;
        PendingQuestion = null;
        _Raise();
    }

    /// <summary>Binds the run's embedded session pane, so a gate report from that pane is trusted as this run's. Does not
    /// raise <see cref="Changed"/> — it changes no visible state, and firing mid-embed would re-enter the body's render.</summary>
    public void BindSession(string paneId) => SessionPaneId = paneId;

    /// <summary>Records a done-gate outcome (and its evidence) the agent reported (AC-153); the body shows it, and <see cref="MarkReady"/> settles on it.</summary>
    public void ReportGate(GateKind gate, AutopilotGateOutcome outcome, string? evidence = null)
    {
        lock (_gateLock)
        {
            _gates[gate] = outcome;
            _evidence[gate] = evidence;
        }

        _Raise();
    }

    /// <summary>
    /// The agent has finished: settle the run to merge-ready when every hard gate passed, else blocked, naming the
    /// gates that did not pass. When the operator has set every gate to Skip there are no hard gates to meet, so the
    /// run is merge-ready by their own explicit opt-out — the default keeps Security hard.
    /// </summary>
    public void MarkReady(string? prUrl = null)
    {
        if (!string.IsNullOrWhiteSpace(prUrl))
        {
            PrUrl = prUrl.Trim();
        }

        List<GateKind> unmet;
        lock (_gateLock)
        {
            unmet = _HardGates()
                .Where(gate => _gates.GetValueOrDefault(gate, AutopilotGateOutcome.Skipped) != AutopilotGateOutcome.Passed)
                .ToList();
        }

        if (unmet.Count > 0)
        {
            Phase = AutopilotRunPhase.Blocked;
            BlockReason = $"Required gate(s) did not pass: {string.Join(", ", unmet)}.";
        }
        else
        {
            Phase = AutopilotRunPhase.MergeReady;
            BlockReason = null;
        }

        _Raise();
    }

    private IEnumerable<GateKind> _HardGates() =>
        Enum.GetValues<GateKind>().Where(gate => settings.Gate(gate) == GateMode.Hard);

    private void _Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
