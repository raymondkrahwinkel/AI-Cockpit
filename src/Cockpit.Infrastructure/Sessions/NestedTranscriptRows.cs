using Cockpit.Core.Sessions;

namespace Cockpit.Infrastructure.Sessions;

// AC-1518: the nested rows of a row list that hears them one at a time, each naming its anchor, put back inside that
// anchor for a reader of the whole list. A row's place in its lane is indexed, so an upsert never scans the lane.
internal sealed class NestedTranscriptRows
{
    private readonly Dictionary<string, List<TranscriptSnapshotEntry>> _lanes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _at = new(StringComparer.Ordinal);

    // True when the row was a nested one and is now held here; a top-level row is left to the caller.
    public bool TryUpsert(TranscriptSnapshotEntry row, IReadOnlyList<TranscriptSnapshotEntry> topLevel)
    {
        if (row.ParentRowId is not { } parentId)
        {
            // An anchor's own upsert carries every nested row it has, newer than the lane it had.
            if (row.SubAgentRows is { } inline && _lanes.ContainsKey(row.Id))
            {
                _Lane(row.Id, inline);
            }

            return false;
        }

        var lane = _lanes.TryGetValue(parentId, out var known)
            ? known
            : _Lane(parentId, topLevel.FirstOrDefault(top => string.Equals(top.Id, parentId, StringComparison.Ordinal))?.SubAgentRows ?? []);
        if (_at.TryGetValue(row.Id, out var at))
        {
            lane[at] = row;
        }
        else
        {
            _at[row.Id] = lane.Count;
            lane.Add(row);
        }

        return true;
    }

    public List<TranscriptSnapshotEntry> Compose(IEnumerable<TranscriptSnapshotEntry> topLevel) =>
        [.. topLevel.Select(row => _lanes.TryGetValue(row.Id, out var lane) ? row with { SubAgentRows = [.. lane] } : row)];

    public void Clear()
    {
        _lanes.Clear();
        _at.Clear();
    }

    private List<TranscriptSnapshotEntry> _Lane(string parentId, IReadOnlyList<TranscriptSnapshotEntry> inline)
    {
        var lane = inline.ToList();
        _lanes[parentId] = lane;
        for (var index = 0; index < lane.Count; index++)
        {
            _at[lane[index].Id] = index;
        }

        return lane;
    }
}
