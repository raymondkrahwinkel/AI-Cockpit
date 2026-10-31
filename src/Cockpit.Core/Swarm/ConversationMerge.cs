using Cockpit.Core.Sessions;

namespace Cockpit.Core.Swarm;

public sealed record MergeField(string Value, DateTimeOffset ChangedAt);

public sealed record MergeSide(SwarmMemberId Member, string Name, IReadOnlyList<TranscriptSnapshotEntry> Rows,
    IReadOnlyDictionary<string, MergeField> Fields);

public sealed record KeptVersion(string Key, MergeField Version, string Origin);

public sealed record ConversationMergeResult(IReadOnlyList<TranscriptSnapshotEntry> Rows,
    IReadOnlyDictionary<string, MergeField> Fields, IReadOnlyList<KeptVersion> Kept, bool Asks);

// AC-1929: what two leaders wrote during a split, merged without a clock. Rows union on their id; what only one side
// has goes under one separator, ordered by timestamp then member id. A field takes the newest version; when both
// sides changed it after the split the other version is kept beside it and asked about once.
public static class ConversationMerge
{
    public const string Divider = "Divider";
    public const string MergingNotice = "2 leaders seen · merging…";

    public static string Separator(string steppingDown) => $"2 leaders seen — {steppingDown} steps down, conversation merged";

    public static ConversationMergeResult Merge(MergeSide winner, MergeSide loser, IReadOnlyList<KeptVersion> kept)
    {
        var winnerIds = winner.Rows.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        var loserIds = loser.Rows.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        var common = winner.Rows.Where(row => loserIds.Contains(row.Id)).ToArray();
        var splitAt = common.Length == 0 ? DateTimeOffset.MinValue : common.Max(row => row.Timestamp);
        var fromLoser = loser.Rows.ToArray();
        var rows = winner.Rows;
        if (fromLoser.Length > 0)
        {
            var fromWinner = winner.Rows.Where(row => !loserIds.Contains(row.Id)).ToArray();
            var apart = fromWinner.Any(row => row.Kind != Divider);
            var added = fromWinner.Select(row => (Row: row, Side: winner)).Concat(fromLoser.Select(row => (Row: row, Side: loser)))
                .OrderBy(item => item.Row.Timestamp).ThenBy(item => item.Side.Member.Value, StringComparer.Ordinal)
                .ThenBy(item => item.Row.Id, StringComparer.Ordinal)
                .Select(item => apart ? item.Row with { WrittenWhileApartOn = item.Row.WrittenWhileApartOn ?? item.Side.Name } : item.Row)
                .ToArray();
            var separator = new TranscriptSnapshotEntry($"swarm-merge-{loser.Member.Value}-{fromLoser[0].Id}", Divider,
                Separator(loser.Name), null, null, null, null, false, added[0].Timestamp);
            rows = apart ? [.. common, separator, .. added] : [.. common, .. added];
        }

        var fields = new Dictionary<string, MergeField>(winner.Fields, StringComparer.Ordinal);
        var keptNow = kept.ToList();
        var asks = false;
        foreach (var (key, theirs) in loser.Fields)
        {
            if (!fields.TryGetValue(key, out var ours))
            {
                fields[key] = theirs;
                continue;
            }

            if (ours.Value == theirs.Value)
            {
                continue;
            }

            var theirsNewer = theirs.ChangedAt > ours.ChangedAt;
            var (newest, other, origin) = theirsNewer ? (theirs, ours, winner.Name) : (ours, theirs, loser.Name);
            fields[key] = newest;
            if (ours.ChangedAt > splitAt && theirs.ChangedAt > splitAt
                && !keptNow.Exists(item => item.Key == key && item.Version.Value == other.Value))
            {
                keptNow.Add(new(key, other, origin));
                asks = true;
            }
        }

        return new(rows, fields, keptNow, asks);
    }
}
