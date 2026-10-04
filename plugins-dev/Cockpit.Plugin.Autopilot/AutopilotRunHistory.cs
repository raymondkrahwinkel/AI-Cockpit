using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Autopilot;

// The history of settled runs: a run that finishes is dropped from the live surface, so without a record it simply
// vanishes ("de run knippert en is dan weg"). Persisted through the plugin's storage so history survives a
// restart, and capped at `MaxEntries` so it cannot grow without bound.
internal sealed class AutopilotRunHistory
{
    private const string StorageKey = "runHistory";
    private const int MaxEntries = 50;
    private readonly IPluginCache _storage;
    private readonly List<AutopilotRunRecord> _records;

    // AC-1418: a run settles off the UI thread now, so the list keeps its own lock against the operator's edits.
    private readonly Lock _lock = new();

    public AutopilotRunHistory(IPluginCache storage)
    {
        _storage = storage;
        _records = storage.Get<List<AutopilotRunRecord>>(StorageKey) ?? [];
    }

    internal static void Migrate(IPluginStorage legacyStorage, IPluginCache cache)
    {
        if (cache.Get<List<AutopilotRunRecord>>(StorageKey) is null && legacyStorage.Get<List<AutopilotRunRecord>>(StorageKey) is { } records)
        {
            cache.Set(StorageKey, records);
        }

        legacyStorage.Remove(StorageKey);
    }

    // Raised when a run is recorded or history is cleared, so the surface re-renders its history section.
    public event Action? Changed;

    // The settled runs, newest first — how the surface lists what has run.
    public IReadOnlyList<AutopilotRunRecord> Items
    {
        get
        {
            lock (_lock)
            {
                return [.. _records];
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _records.Count;
            }
        }
    }

    // Records a settled run at the front (newest first), trimming the oldest past the cap.
    public void Add(AutopilotRunRecord record)
    {
        lock (_lock)
        {
            _records.Insert(0, record);
            if (_records.Count > MaxEntries)
            {
                _records.RemoveRange(MaxEntries, _records.Count - MaxEntries);
            }

            _Save();
        }

        Changed?.Invoke();
    }

    // Replaces `original` with `replacement` — the path an operator's manual reclassification writes through
    // (AC-347). Matched on the record instance, deliberately not on a position: a run that settles while the menu
    // is open shifts every index down one, so a position-keyed write would silently edit a different run.
    public void Replace(AutopilotRunRecord original, AutopilotRunRecord replacement)
    {
        lock (_lock)
        {
            var index = _records.FindIndex(candidate => ReferenceEquals(candidate, original));
            if (index < 0)
            {
                return;
            }

            _records[index] = replacement;
            _Save();
        }

        Changed?.Invoke();
    }

    // Clears the history — the operator emptied it.
    public void Clear()
    {
        lock (_lock)
        {
            if (_records.Count == 0)
            {
                return;
            }

            _records.Clear();
            _Save();
        }

        Changed?.Invoke();
    }

    // Under _lock; the caller raises Changed once it let go.
    private void _Save() => _storage.Set(StorageKey, _records);
}
