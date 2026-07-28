using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The history of settled runs: a run that finishes — merge-ready or blocked — is dropped from the
/// live surface, so without a record it simply vanishes ("de run knippert en is dan weg"). Each settled run is recorded
/// here, newest first, so the surface can show what was run and how it ended. Persisted through the plugin's storage so
/// history survives a restart, and capped at <see cref="MaxEntries"/> so it cannot grow without bound.
/// </summary>
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

    /// <summary>Raised when a run is recorded or history is cleared, so the surface re-renders its history section.</summary>
    public event Action? Changed;

    /// <summary>The settled runs, newest first — how the surface lists what has run.</summary>
    public IReadOnlyList<AutopilotRunRecord> Items => _records;

    public int Count => _records.Count;

    /// <summary>Records a settled run at the front (newest first), trimming the oldest past the cap.</summary>
    public void Add(AutopilotRunRecord record)
    {
        _records.Insert(0, record);
        if (_records.Count > MaxEntries)
        {
            _records.RemoveRange(MaxEntries, _records.Count - MaxEntries);
        }

        _Save();
    }

    /// <summary>
    /// Replaces <paramref name="original"/> with <paramref name="replacement"/> — the path an operator's manual
    /// reclassification writes through (AC-347). Matched on the record instance, deliberately not on a position: a run
    /// that settles while the menu is open inserts at the front and shifts every index down one, so a position-keyed
    /// write would land the edit on a <em>different</em> run without any bound being exceeded — a silently wrong figure,
    /// which is the one failure this measurement exists to rule out. A record the history no longer holds (cleared, or
    /// aged past the cap) is a no-op.
    /// </summary>
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

    /// <summary>Clears the history — the operator emptied it.</summary>
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
