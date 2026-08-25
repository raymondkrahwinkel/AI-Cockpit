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

    public AutopilotRunQueue(IPluginStorage storage)
    {
        _storage = storage;
        _plans = storage.Get<List<AutopilotPlan>>(StorageKey) ?? [];
    }

    // Raised when the queue changes, so the surface re-renders and the executor re-checks whether it can start one.
    public event Action? Changed;

    // The queued plans in run order — the front runs next.
    public IReadOnlyList<AutopilotPlan> Items => _plans;

    public int Count => _plans.Count;

    // Adds an approved plan to the back of the queue.
    public void Enqueue(AutopilotPlan plan)
    {
        _plans.Add(plan);
        _Save();
    }

    // Takes the front plan to run, or false when the queue is empty.
    public bool TryDequeue(out AutopilotPlan? plan)
    {
        if (_plans.Count == 0)
        {
            plan = null;
            return false;
        }

        plan = _plans[0];
        _plans.RemoveAt(0);
        _Save();
        return true;
    }

    // Drops the queued entry at `index` — the operator removed a run before it started.
    public void RemoveAt(int index)
    {
        if (index >= 0 && index < _plans.Count)
        {
            _plans.RemoveAt(index);
            _Save();
        }
    }

    // Moves the entry at `index` one place earlier so it runs sooner; a no-op at the front.
    public void MoveUp(int index) => _Swap(index, index - 1);

    // Moves the entry at `index` one place later so it runs afterwards; a no-op at the back.
    public void MoveDown(int index) => _Swap(index, index + 1);

    private void _Swap(int a, int b)
    {
        if (a >= 0 && a < _plans.Count && b >= 0 && b < _plans.Count && a != b)
        {
            (_plans[a], _plans[b]) = (_plans[b], _plans[a]);
            _Save();
        }
    }

    private void _Save()
    {
        _storage.Set(StorageKey, _plans);
        Changed?.Invoke();
    }
}
