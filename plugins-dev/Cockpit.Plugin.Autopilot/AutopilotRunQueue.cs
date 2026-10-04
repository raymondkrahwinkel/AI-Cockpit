using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Autopilot;

// The queue of approved runs waiting to execute (AC-174). The operator can stage several plans up front; up to
// `AutopilotSettings.MaxConcurrentRuns` execute at once and the rest wait here in order. Persisted through the
// plugin's storage so a staged queue survives a restart; the operator can reorder or drop entries before they run.
internal sealed class AutopilotRunQueue
{
    private const string StorageKey = "runQueue";
    private readonly IPluginStorage _storage;
    private readonly List<AutopilotPlan> _plans;

    // AC-1418: the UI thread no longer serializes the operator's edits with the pump, so the list keeps its own lock.
    private readonly Lock _lock = new();

    public AutopilotRunQueue(IPluginStorage storage)
    {
        _storage = storage;
        _plans = storage.Get<List<AutopilotPlan>>(StorageKey) ?? [];
    }

    // Raised when the queue changes, so the surface re-renders and the executor re-checks whether it can start one.
    public event Action? Changed;

    // The queued plans in run order — the front runs next.
    public IReadOnlyList<AutopilotPlan> Items
    {
        get
        {
            lock (_lock)
            {
                return [.. _plans];
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _plans.Count;
            }
        }
    }

    // Adds an approved plan to the back of the queue.
    public void Enqueue(AutopilotPlan plan)
    {
        lock (_lock)
        {
            _plans.Add(plan);
            _Save();
        }

        Changed?.Invoke();
    }

    // Takes the front plan to run, or false when the queue is empty.
    public bool TryDequeue(out AutopilotPlan? plan)
    {
        lock (_lock)
        {
            if (_plans.Count == 0)
            {
                plan = null;
                return false;
            }

            plan = _plans[0];
            _plans.RemoveAt(0);
            _Save();
        }

        Changed?.Invoke();
        return true;
    }

    // Drops the queued entry at `index` — the operator removed a run before it started.
    public void RemoveAt(int index)
    {
        lock (_lock)
        {
            if (index < 0 || index >= _plans.Count)
            {
                return;
            }

            _plans.RemoveAt(index);
            _Save();
        }

        Changed?.Invoke();
    }

    // Moves the entry at `index` one place earlier so it runs sooner; a no-op at the front.
    public void MoveUp(int index) => _Swap(index, index - 1);

    // Moves the entry at `index` one place later so it runs afterwards; a no-op at the back.
    public void MoveDown(int index) => _Swap(index, index + 1);

    private void _Swap(int a, int b)
    {
        lock (_lock)
        {
            if (a < 0 || a >= _plans.Count || b < 0 || b >= _plans.Count || a == b)
            {
                return;
            }

            (_plans[a], _plans[b]) = (_plans[b], _plans[a]);
            _Save();
        }

        Changed?.Invoke();
    }

    // Under _lock; the caller raises Changed once it let go.
    private void _Save() => _storage.Set(StorageKey, _plans);
}
