using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The queue of approved runs waiting to execute (AC-174, Raymond). The operator can stage several plans up front; up to
/// <see cref="AutopilotSettings.MaxConcurrentRuns"/> execute at once and the rest wait here, in order, to be worked
/// through one after another. Persisted through the plugin's storage so a staged queue survives a restart. The operator
/// can reorder entries or drop them before they run.
/// </summary>
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

    /// <summary>Raised when the queue changes, so the surface re-renders and the executor re-checks whether it can start one.</summary>
    public event Action? Changed;

    /// <summary>The queued plans in run order — the front runs next.</summary>
    public IReadOnlyList<AutopilotPlan> Items => _plans;

    public int Count => _plans.Count;

    /// <summary>Adds an approved plan to the back of the queue.</summary>
    public void Enqueue(AutopilotPlan plan)
    {
        _plans.Add(plan);
        _Save();
    }

    /// <summary>Takes the front plan to run, or false when the queue is empty.</summary>
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

    /// <summary>Drops the queued entry at <paramref name="index"/> — the operator removed a run before it started.</summary>
    public void RemoveAt(int index)
    {
        if (index >= 0 && index < _plans.Count)
        {
            _plans.RemoveAt(index);
            _Save();
        }
    }

    /// <summary>Moves the entry at <paramref name="index"/> one place earlier so it runs sooner; a no-op at the front.</summary>
    public void MoveUp(int index) => _Swap(index, index - 1);

    /// <summary>Moves the entry at <paramref name="index"/> one place later so it runs afterwards; a no-op at the back.</summary>
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
