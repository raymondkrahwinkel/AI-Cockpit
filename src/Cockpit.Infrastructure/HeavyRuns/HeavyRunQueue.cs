using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.HeavyRuns;
using Cockpit.Plugins.Abstractions.HeavyRuns;
using Cockpit.Plugins.Abstractions.StatusBar;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.HeavyRuns;

// AC-1535: the machine-wide queue for heavy runs; `start_run` and local-ci's gate both take their place here. Who gets
// in is `HeavyRunAdmission`'s call; this keeps the places, gives them back (run over, owner gone, stopped by hand),
// learns what each kind cost, and is the "Heavy runs" status item. In memory: a restart empties it.
public sealed class HeavyRunQueue : ISupervisedActivitySource, ISingletonService, IDisposable
{
    // As long as the load average needs to show a just-admitted run: until then its expected cost is counted on top,
    // and a run shorter than this never moved the average enough to learn its cost from.
    private static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _settingsAccess = new(1, 1);
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

        // Read from disk here, outside the lock: a slow volume must not hold up the status bar or another request.
        var slot = new Slot(this, request, DateTimeOffset.UtcNow, HeavyRunCostStore.Key(request.Kind, request.WorkingDirectory))
        {
            FreeDiskBytes = _resources.FreeDiskBytes(request.WorkingDirectory),
        };
        lock (_gate)
        {
            if (request.Purpose == HeavyRunPurpose.Operator)
            {
                slot.Admit(_resources.Current, null, DateTimeOffset.UtcNow);
                _running.Add(slot);
            }
            else
            {
                _waiting.Add(slot);
            }
        }

        slot.Started();
        try
        {
            _Pump();
        }
        catch (Exception exception)
        {
            // The place is taken already and its owner will give it back; a failed look now is retried on the next sample.
            _logger.LogError(exception, "The heavy-run queue failed to look at its runs after a request.");
        }

        _ = _RefreshSettingsAsync();
        return slot;
    }

    // What `run_status` reports for a run the queue holds: "queued" with its reason, or "running"; null otherwise.
    public (string Status, string? Reason)? StatusOf(string runId)
    {
        lock (_gate)
        {
            if (_waiting.FirstOrDefault(slot => slot.Request.RunId == runId) is { } waiting)
            {
                return ("queued", waiting.WaitReason);
            }

            return _running.Any(slot => slot.Request.RunId == runId) ? ("running", null) : null;
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
        try
        {
            _Tick();
        }
        catch (Exception exception)
        {
            // Raised on the sampler's timer thread: an escape there takes the cockpit down, a skipped tick does not.
            _logger.LogError(exception, "The heavy-run queue failed to look at its runs; the next sample will try again.");
        }
    }

    private void _Tick()
    {
        var load = _resources.Current;
        var live = _live?.LiveSessionIds;
        List<Slot> held;
        lock (_gate)
        {
            held = [.. _waiting, .. _running];
        }

        var freeDisk = held.Select(slot => _resources.FreeDiskBytes(slot.Request.WorkingDirectory)).ToList();
        List<Slot> gone = [];
        lock (_gate)
        {
            for (var index = 0; index < held.Count; index++)
            {
                held[index].FreeDiskBytes = freeDisk[index];
            }

            foreach (var slot in _running)
            {
                slot.Observe(load);
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
                    _costs.Get(slot.CostKey),
                    slot.FreeDiskBytes)).ToList();
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
                slot.Admit(_resources.Current, candidates[index].Cost, now);
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
        if (wasRunning && slot.Measured(DateTimeOffset.UtcNow, WarmUp) is { } cost)
        {
            try
            {
                await _costs.RecordAsync(slot.CostKey, cost).ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                _logger.LogWarning(exception, "Saving a heavy run's measured cost failed; the next run measures it again.");
            }
        }

        _Pump();
    }

    // The only self-correcting part: a run that failed the way an overloaded machine makes runs fail, while the
    // machine was that busy, lowers the ceilings one step — never below the floor.
    private async Task _ReportFailureAsync(Slot slot, string output)
    {
        if (!HeavyRunAdmission.LooksOverloaded(output))
        {
            return;
        }

        await _settingsAccess.WaitAsync().ConfigureAwait(false);
        try
        {
            var lowered = _settings.Lowered();
            if (slot.PeakCpuPercent <= _settings.OverloadedAbovePercent || lowered == _settings)
            {
                return;
            }

            await _settingsStore.SaveAsync(lowered).ConfigureAwait(false);
            _settings = lowered;
            _logger.LogWarning(
                "A heavy run failed like an overloaded machine at {Cpu:0}% CPU; the ceilings are now {CpuCeiling:0}% CPU and {MemoryCeiling:0}% memory.",
                slot.PeakCpuPercent,
                lowered.CpuCeilingPercent,
                lowered.MemoryCeilingPercent);
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "Lowering the heavy-run ceilings failed; they stay where they were.");
        }
        finally
        {
            _settingsAccess.Release();
        }
    }

    private async Task _RefreshSettingsAsync()
    {
        await _settingsAccess.WaitAsync().ConfigureAwait(false);
        try
        {
            _settings = await _settingsStore.LoadAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Reading the heavy-run settings failed; the queue keeps its current ceilings.");
            return;
        }
        finally
        {
            _settingsAccess.Release();
        }

        _Pump();
    }

    private static string _Since(DateTimeOffset startedAt)
    {
        var elapsed = DateTimeOffset.UtcNow - startedAt;
        return elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s" : $"{elapsed.Seconds}s";
    }

    private sealed class Slot(HeavyRunQueue queue, HeavyRunRequest request, DateTimeOffset enqueuedAt, string costKey) : IHeavyRunSlot
    {
        private readonly TaskCompletionSource _admitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _revoked = new();
        private MachineLoad? _baseline;
        private long? _baselineFreeDisk;
        private long _peakUsedMemory;
        private long? _lowestFreeDisk;

        public HeavyRunRequest Request { get; } = request;

        public DateTimeOffset EnqueuedAt { get; } = enqueuedAt;

        public string CostKey { get; } = costKey;

        public long? FreeDiskBytes { get; set; }

        public DateTimeOffset AdmittedAt { get; private set; }

        public HeavyRunCost? ExpectedCost { get; private set; }

        public double PeakCpuPercent { get; private set; }

        public string? WaitReason { get; set; }

        public Task Admitted => _admitted.Task;

        public CancellationToken Revoked => _revoked.Token;

        public void Admit(MachineLoad load, HeavyRunCost? expected, DateTimeOffset now)
        {
            (_baseline, _baselineFreeDisk, ExpectedCost, AdmittedAt, WaitReason) = (load, FreeDiskBytes, expected, now, null);
            (PeakCpuPercent, _peakUsedMemory, _lowestFreeDisk) = (load.CpuPercent, load.UsedMemoryBytes, FreeDiskBytes);
        }

        public void Started()
        {
            if (_baseline is not null)
            {
                _admitted.TrySetResult();
            }
        }

        public void Observe(MachineLoad load)
        {
            PeakCpuPercent = Math.Max(PeakCpuPercent, load.CpuPercent);
            _peakUsedMemory = Math.Max(_peakUsedMemory, load.UsedMemoryBytes);
            _lowestFreeDisk = FreeDiskBytes is { } free && (_lowestFreeDisk is null || free < _lowestFreeDisk) ? free : _lowestFreeDisk;
        }

        // What this run added at its peak over the load it was admitted at; with others running beside it this also
        // counts them, which errs towards waiting. A stopped run, or one too short to show, teaches nothing.
        public HeavyRunCost? Measured(DateTimeOffset now, TimeSpan warmUp) =>
            _baseline is { } baseline && !_revoked.IsCancellationRequested && now - AdmittedAt >= warmUp
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
