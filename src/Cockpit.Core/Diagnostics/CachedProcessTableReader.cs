using Cockpit.Core.Abstractions.Diagnostics;
using System.Diagnostics;

namespace Cockpit.Core.Diagnostics;

public sealed class CachedProcessTableReader : IProcessTableReader
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(3);
    private readonly IProcessTableReader _inner;
    private readonly Lock _lock = new();
    private ProcessTableSnapshotRows? _rows;
    private long _readAt;

    internal int SnapshotsBuilt { get; private set; }

    public CachedProcessTableReader(IProcessTableReader inner) => _inner = inner;

    public IReadOnlyList<ProcessRow> Read()
    {
        lock (_lock)
        {
            if (_rows is not null && Stopwatch.GetElapsedTime(_readAt) < Lifetime) return _rows;
            _rows = new ProcessTableSnapshotRows(_inner.Read());
            _readAt = Stopwatch.GetTimestamp();
            SnapshotsBuilt++;
            return _rows;
        }
    }
}

internal sealed class ProcessTableSnapshotRows(IReadOnlyList<ProcessRow> rows) : IReadOnlyList<ProcessRow>
{
    public ProcessTableSnapshot Snapshot { get; } = new(rows);
    public int Count => rows.Count;
    public ProcessRow this[int index] => rows[index];
    public IEnumerator<ProcessRow> GetEnumerator() => rows.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
