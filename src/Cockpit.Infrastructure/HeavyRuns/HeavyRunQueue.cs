using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.HeavyRuns;
using Cockpit.Plugins.Abstractions.HeavyRuns;
using Cockpit.Plugins.Abstractions.StatusBar;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.HeavyRuns;

// AC-1535: the machine-wide queue for heavy runs — `start_run` and local-ci's gate both take their place here. Who
// gets in is `HeavyRunAdmission`'s call on the measured load; this keeps the places, gives them back (run over, owner
// gone, stopped by hand), learns what each kind cost, and is the "Heavy runs" status item. In memory: a restart empties it.
public sealed class HeavyRunQueue : ISupervisedActivitySource, ISingletonService, IDisposable
{
    // As long as the load average needs to show a just-admitted run: until then its expected cost is counted on top.
    private static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly List<Slot> _waiting = [];
    private readonly List<Slot> _running = [];
    private readonly IMachineResources _resources;
    private readonly IHeavyRunSettingsStore _settingsStore;
    private readonly HeavyRunCostStore _costs;
    private readonly ILiveSessionRegistry? _live;
    private readonly ILogger<HeavyRunQueue> _logger;
    private HeavyRunSettings _settings = new();

    public HeavyRunQueue(
        IMachineResources resources,
        IHeavyRunSettingsStore settingsStore,
        HeavyRunCostStore costs,
        ILiveSessionRegistry? live = null,
        ILogger<HeavyRunQueue>? logger = null)
    {
        _resources = resources;
        _settingsStore = settingsStore;
        _costs = costs;
        _live = live;
        _logger = logger ?? NullLogger<HeavyRunQueue>.Instance;
        _resources.Sampled += _OnSampled;
    }

    public string Label => "Heavy runs";

    public event Action? Changed;

    public IHeavyRunSlot Request(HeavyRunRequest request)
    {
        if (request.Kind == HeavyRunKind.Light)
        {
            return AdmittedHeavyRunSlot.Instance;
        }

        var slot = new Slot(this, request, DateTimeOffset.UtcNow);
        lock (_gate)
        {
            if (request.Purpose == HeavyRunPurpose.Operator)
            {
                slot.Admit(_resources.Current, _resources.FreeDiskBytes(request.WorkingDirectory), null, DateTimeOffset.UtcNow);
                _running.Add(slot);
            }
            else
            {
                _waiting.Add(slot);
            }
        }

        slot.Started();

        _Pump();
        _ = _RefreshSettingsAsync();
        return slot;
    }

    // Why a queued run is still waiting, for `run_status`; null when it is not waiting (running, finished or unknown).
    public string? WaitReasonFor(string runId)
    {
        lock (_gate)
        {
            return _waiting.FirstOrDefault(slot => slot.Request.RunId == runId)?.WaitReason;
        }
    }

    public IReadOnlyList<SupervisedActivity> Snapshot()
    {
        List<Slot> running, waiting;
        lock (_gate)
        {
            running = [.. _running];
            waiting = [.. _Ordered()];
        }

        return
        [
            .. running.Select(slot => new SupervisedActivity(
                slot.Request.RunId,
                slot.Request.Purpose == HeavyRunPurpose.Operator ? $"{slot.Request.Label} (operator)" : slot.Request.Label,
                [
                    new ActivityDetail("Session", slot.Request.PaneId),
                    new ActivityDetail("Running for", _Since(slot.AdmittedAt)),
                ],
                () =>
                {
                    slot.Revoke();
                    return Task.CompletedTask;
                })),
            .. waiting.Select(slot => new SupervisedActivity(
                slot.Request.RunId,
                slot.Request.Label,
                [
                    new ActivityDetail("Session", slot.Request.PaneId),
                    new ActivityDetail("Waiting", slot.WaitReason ?? "waiting"),
                ],
                () => slot.DisposeAsync().AsTask())),
        ];
    }

    public void Dispose() => _resources.Sampled -= _OnSampled;

    private void _OnSampled()
    {
        var load = _resources.Current;
        var live = _live?.LiveSessionIds;
        List<Slot> gone = [];
        lock (_gate)
        {
            foreach (var slot in _running)
            {
                slot.Observe(load, _resources.FreeDiskBytes(slot.Request.WorkingDirectory));
            }

            // A place belongs to the session that asked for it: once that session is gone, so is its place.
            if (live is not null)
            {
                gone = [.. _waiting.Concat(_running)
                    .Where(slot => slot.Request.Purpose != HeavyRunPurpose.Operator && !live.Contains(slot.Request.PaneId))];
                _waiting.RemoveAll(gone.Contains);
                _running.RemoveAll(gone.Contains);
            }
        }

        foreach (var slot in gone)
        {
            slot.Revoke();
        }

        _Pump();
    }

    private void _Pump()
    {
        List<Slot> admitted = [];
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            while (_waiting.Count > 0)
            {
                var candidates = _waiting.Select(slot => new HeavyRunCandidate(
                    slot.Request.Kind.ToString().ToLowerInvariant(),
                    slot.Request.Purpose == HeavyRunPurpose.PreMerge,
                    slot.EnqueuedAt,
                    _costs.Get(slot.Request.Kind, slot.Request.WorkingDirectory),
                    _resources.FreeDiskBytes(slot.Request.WorkingDirectory))).ToList();
                var decision = HeavyRunAdmission.Next(candidates, _Projected(now), _settings, _running.Count > 0, now);
                if (decision.Admit is not { } index)
                {
                    var ordered = _Ordered();
                    for (var place = 0; place < ordered.Count; place++)
                    {
                        ordered[place].WaitReason = place == 0 ? decision.WaitReason : $"{decision.WaitReason}, {place} ahead of you";
                    }

                    break;
                }

                var slot = _waiting[index];
                _waiting.RemoveAt(index);
                slot.Admit(_resources.Current, _resources.FreeDiskBytes(slot.Request.WorkingDirectory), candidates[index].Cost, now);
                _running.Add(slot);
                admitted.Add(slot);
            }
        }

        foreach (var slot in admitted)
        {
            slot.Started();
        }

        Changed?.Invoke();
    }

    // The measured load plus what the runs admitted within the warm-up are expected to add but cannot show yet.
    private MachineLoad _Projected(DateTimeOffset now)
    {
        var load = _resources.Current;
        var fresh = _running.Where(slot => now - slot.AdmittedAt < WarmUp).Select(slot => slot.ExpectedCost).OfType<HeavyRunCost>().ToList();
        return load with
        {
            CpuPercent = load.CpuPercent + fresh.Sum(cost => cost.CpuPercent),
            AvailableMemoryBytes = load.AvailableMemoryBytes - fresh.Sum(cost => cost.MemoryBytes),
        };
    }

    private List<Slot> _Ordered()
    {
        var order = HeavyRunAdmission.Order(
            [.. _waiting.Select(slot => new HeavyRunCandidate(string.Empty, slot.Request.Purpose == HeavyRunPurpose.PreMerge, slot.EnqueuedAt, null, null))],
            _settings,
            DateTimeOffset.UtcNow);
        return [.. order.Select(index => _waiting[index])];
    }

    private async ValueTask _ReleaseAsync(Slot slot)
    {
        bool wasRunning;
        lock (_gate)
        {
            _waiting.Remove(slot);
            wasRunning = _running.Remove(slot);
        }

        slot.Abandon();
        if (wasRunning && slot.Measured() is { } cost)
        {
            await _costs.RecordAsync(slot.Request.Kind, slot.Request.WorkingDirectory, cost).ConfigureAwait(false);
        }

        _Pump();
    }

    // The only self-correcting part: a run that failed the way an overloaded machine makes runs fail, while the
    // machine was that busy, lowers the ceilings one step — never below the floor.
    private async Task _ReportFailureAsync(Slot slot, string output)
    {
        if (!HeavyRunAdmission.LooksOverloaded(output) || slot.PeakCpuPercent <= _settings.OverloadedAbovePercent)
        {
            return;
        }

        var lowered = _settings.Lowered();
        if (lowered == _settings)
        {
            return;
        }

        _settings = lowered;
        _logger.LogWarning(
            "A heavy run failed like an overloaded machine at {Cpu:0}% CPU; the ceilings are now {CpuCeiling:0}% CPU and {MemoryCeiling:0}% memory.",
            slot.PeakCpuPercent,
            lowered.CpuCeilingPercent,
            lowered.MemoryCeilingPercent);
        await _settingsStore.SaveAsync(lowered).ConfigureAwait(false);
    }

    private async Task _RefreshSettingsAsync()
    {
        try
        {
            _settings = await _settingsStore.LoadAsync().ConfigureAwait(false);
            _Pump();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Reading the heavy-run settings failed; the queue keeps its current ceilings.");
        }
    }

    private static string _Since(DateTimeOffset startedAt)
    {
        var elapsed = DateTimeOffset.UtcNow - startedAt;
        return elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s" : $"{elapsed.Seconds}s";
    }

    private sealed class Slot(HeavyRunQueue queue, HeavyRunRequest request, DateTimeOffset enqueuedAt) : IHeavyRunSlot
    {
        private readonly TaskCompletionSource _admitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _revoked = new();
        private MachineLoad? _baseline;
        private long? _baselineFreeDisk;
        private long _peakUsedMemory;
        private long? _lowestFreeDisk;

        public HeavyRunRequest Request { get; } = request;

        public DateTimeOffset EnqueuedAt { get; } = enqueuedAt;

        public DateTimeOffset AdmittedAt { get; private set; }

        public HeavyRunCost? ExpectedCost { get; private set; }

        public double PeakCpuPercent { get; private set; }

        public string? WaitReason { get; set; }

        public Task Admitted => _admitted.Task;

        public CancellationToken Revoked => _revoked.Token;

        public void Admit(MachineLoad load, long? freeDisk, HeavyRunCost? expected, DateTimeOffset now)
        {
            (_baseline, _baselineFreeDisk, ExpectedCost, AdmittedAt, WaitReason) = (load, freeDisk, expected, now, null);
            (PeakCpuPercent, _peakUsedMemory, _lowestFreeDisk) = (load.CpuPercent, load.UsedMemoryBytes, freeDisk);
        }

        public void Started()
        {
            if (_baseline is not null)
            {
                _admitted.TrySetResult();
            }
        }

        public void Observe(MachineLoad load, long? freeDisk)
        {
            PeakCpuPercent = Math.Max(PeakCpuPercent, load.CpuPercent);
            _peakUsedMemory = Math.Max(_peakUsedMemory, load.UsedMemoryBytes);
            _lowestFreeDisk = freeDisk is { } free && (_lowestFreeDisk is null || free < _lowestFreeDisk) ? free : _lowestFreeDisk;
        }

        // What this run added at its peak over the load it was admitted at; with others running beside it this also
        // counts them, which errs towards waiting rather than towards a failed gate.
        public HeavyRunCost? Measured() => _baseline is { } baseline
            ? new HeavyRunCost(
                Math.Max(0, PeakCpuPercent - baseline.CpuPercent),
                Math.Max(0, _peakUsedMemory - baseline.UsedMemoryBytes),
                _baselineFreeDisk is { } before && _lowestFreeDisk is { } lowest ? Math.Max(0, before - lowest) : 0)
            : null;

        public void Revoke()
        {
            _admitted.TrySetCanceled();
            _revoked.Cancel();
        }

        public void Abandon() => _admitted.TrySetCanceled();

        public Task ReportFailureAsync(string output) => queue._ReportFailureAsync(this, output);

        // `_revoked` is left to the collector: a Stop pressed after the run ended must still find something to cancel.
        public ValueTask DisposeAsync() => queue._ReleaseAsync(this);
    }
}
