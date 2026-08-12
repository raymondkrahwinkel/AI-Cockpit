namespace Cockpit.Core.Mentions;

// Ranks candidate paths against a fuzzy @-mention query. A pure subsequence scorer, not a general fuzzy-finder
// library — AC-740's own research found nothing worth a dependency for this, and it needs to follow our own
// ordering preference (filename over path, prefix over mid-string) rather than a library's.
public static class MentionMatcher
{
    // The best `max` paths in `candidates` for `query`, highest-score first. An empty query returns the first
    // `max` candidates unranked (browsing on a bare '@'); a candidate that doesn't contain `query` as a
    // case-insensitive subsequence is dropped.
    public static IReadOnlyList<string> Rank(IReadOnlyList<string> candidates, string query, int max)
    {
        if (max <= 0)
        {
            return [];
        }

        if (string.IsNullOrEmpty(query))
        {
            return candidates.Take(max).ToList();
        }

        var scored = new List<(string Path, int Score)>();
        foreach (var candidate in candidates)
        {
            if (_Score(candidate, query) is { } score)
            {
                scored.Add((candidate, score));
            }
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Path.Length)
            .ThenBy(s => s.Path, StringComparer.Ordinal)
            .Take(max)
            .Select(s => s.Path)
            .ToList();
    }

    // Greedy left-to-right subsequence match: every query character must appear in `path`, in order,
    // case-insensitively. Null on no match. A hit inside the filename (after the last '/') outweighs one in a
    // directory segment, and a hit at the start of a segment outweighs one mid-segment.
    private static int? _Score(string path, string query)
    {
        var fileNameStart = path.LastIndexOf('/') + 1;
        var qi = 0;
        var score = 0;

        for (var pi = 0; pi < path.Length && qi < query.Length; pi++)
        {
            if (char.ToLowerInvariant(path[pi]) != char.ToLowerInvariant(query[qi]))
            {
                continue;
            }

            score += pi >= fileNameStart ? 10 : 1;
            if (pi == 0 || path[pi - 1] == '/')
            {
                score += 5;
            }

            qi++;
        }

        return qi == query.Length ? score : null;
    }
}
