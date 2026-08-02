using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Autopilot;

// The history of settled runs: a run that finishes — merge-ready or blocked — is dropped from the
// live surface, so without a record it simply vanishes ("de run knippert en is dan weg"). Each settled run is recorded
// here, newest first, so the surface can show what was run and how it ended. Persisted through the plugin's storage so
// history survives a restart, and capped at `MaxEntries` so it cannot grow without bound.
internal sealed class AutopilotRunHistory
{
    private const string StorageKey = "runHistory";
    private const int MaxEntries = 50;
    private readonly IPluginStorage _storage;
    private readonly List<AutopilotRunRecord> _records;

    public AutopilotRunHistory(IPluginStorage storage)
    {
        _storage = storage;
        _records = storage.Get<List<AutopilotRunRecord>>(StorageKey) ?? [];
    }

    // Raised when a run is recorded or history is cleared, so the surface re-renders its history section.
    public event Action? Changed;

    // The settled runs, newest first — how the surface lists what has run.
    public IReadOnlyList<AutopilotRunRecord> Items => _records;

    public int Count => _records.Count;

    // Records a settled run at the front (newest first), trimming the oldest past the cap.
    public void Add(AutopilotRunRecord record)
    {
        _records.Insert(0, record);
        if (_records.Count > MaxEntries)
        {
            _records.RemoveRange(MaxEntries, _records.Count - MaxEntries);
        }

        _Save();
    }

    // Replaces `original` with `replacement` — the path an operator's manual
    // reclassification writes through (AC-347). Matched on the record instance, deliberately not on a position: a run
    // that settles while the menu is open inserts at the front and shifts every index down one, so a position-keyed
    // write would land the edit on a *different* run without any bound being exceeded — a silently wrong figure,
    // which is the one failure this measurement exists to rule out. A record the history no longer holds (cleared, or
    // aged past the cap) is a no-op.
    public void Replace(AutopilotRunRecord original, AutopilotRunRecord replacement)
    {
        var index = _records.FindIndex(candidate => ReferenceEquals(candidate, original));
        if (index < 0)
        {
            return;
        }

        _records[index] = replacement;
        _Save();
    }

    // Clears the history — the operator emptied it.
    public void Clear()
    {
        if (_records.Count == 0)
        {
            return;
        }

        _records.Clear();
        _Save();
    }

    private void _Save()
    {
        _storage.Set(StorageKey, _records);
        Changed?.Invoke();
    }
}
