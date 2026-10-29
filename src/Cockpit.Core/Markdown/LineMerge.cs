namespace Cockpit.Core.Markdown;

public enum LineChange { Same, OnlyTheirs, OnlyMine }

public readonly record struct ComparedLine(LineChange Change, string Text);

// Pure per-line three-way merge and two-way compare, used when a save finds the source changed underneath.
public static class LineMerge
{
    // Beyond this many cells the changed middle is not aligned; it is treated as one changed region.
    private const long MaxCells = 4_000_000;

    // Where both sides changed the same stretch, both stay, yours first.
    public static IReadOnlyList<string> Merge(IReadOnlyList<string> baseLines, IReadOnlyList<string> theirs, IReadOnlyList<string> mine)
        => Merge(baseLines, theirs, mine, out _);

    public static IReadOnlyList<string> Merge(IReadOnlyList<string> baseLines, IReadOnlyList<string> theirs, IReadOnlyList<string> mine, out bool overlap)
    {
        overlap = false;
        var theirMatch = _Match(baseLines, theirs).ToDictionary(pair => pair.A, pair => pair.B);
        var mineMatch = _Match(baseLines, mine).ToDictionary(pair => pair.A, pair => pair.B);
        var merged = new List<string>();
        int baseAt = 0, theirAt = 0, mineAt = 0;
        for (var line = 0; line <= baseLines.Count; line++)
        {
            var stable = line < baseLines.Count && theirMatch.ContainsKey(line) && mineMatch.ContainsKey(line);
            if (line < baseLines.Count && !stable)
            {
                continue;
            }
            var theirEnd = stable ? theirMatch[line] : theirs.Count;
            var mineEnd = stable ? mineMatch[line] : mine.Count;
            var region = _Resolve(_Slice(baseLines, baseAt, line), _Slice(theirs, theirAt, theirEnd), _Slice(mine, mineAt, mineEnd), out var regionOverlap);
            overlap |= regionOverlap;
            merged.AddRange(region);
            if (stable)
            {
                merged.Add(baseLines[line]);
            }
            baseAt = line + 1;
            theirAt = theirEnd + 1;
            mineAt = mineEnd + 1;
        }
        return merged;
    }

    public static IReadOnlyList<ComparedLine> Compare(IReadOnlyList<string> theirs, IReadOnlyList<string> mine)
    {
        var result = new List<ComparedLine>();
        int theirAt = 0, mineAt = 0;
        foreach (var (theirIndex, mineIndex) in _Match(theirs, mine).Append((theirs.Count, mine.Count)))
        {
            result.AddRange(_Slice(theirs, theirAt, theirIndex).Select(text => new ComparedLine(LineChange.OnlyTheirs, text)));
            result.AddRange(_Slice(mine, mineAt, mineIndex).Select(text => new ComparedLine(LineChange.OnlyMine, text)));
            if (theirIndex < theirs.Count)
            {
                result.Add(new ComparedLine(LineChange.Same, theirs[theirIndex]));
            }
            theirAt = theirIndex + 1;
            mineAt = mineIndex + 1;
        }
        return result;
    }

    private static string[] _Resolve(string[] baseRegion, string[] theirs, string[] mine, out bool overlap)
    {
        overlap = false;
        if (theirs.SequenceEqual(baseRegion) || theirs.SequenceEqual(mine))
        {
            return mine;
        }
        if (mine.SequenceEqual(baseRegion))
        {
            return theirs;
        }
        if (baseRegion.Length == 0)
        {
            overlap = true;
            return [.. mine, .. theirs];
        }
        // One side only added lines next to the stretch the other changed: keep the change and the addition.
        if (theirs.Take(baseRegion.Length).SequenceEqual(baseRegion))
        {
            return [.. mine, .. theirs.Skip(baseRegion.Length)];
        }
        if (theirs.TakeLast(baseRegion.Length).SequenceEqual(baseRegion))
        {
            return [.. theirs.SkipLast(baseRegion.Length), .. mine];
        }
        if (mine.Take(baseRegion.Length).SequenceEqual(baseRegion))
        {
            return [.. theirs, .. mine.Skip(baseRegion.Length)];
        }
        if (mine.TakeLast(baseRegion.Length).SequenceEqual(baseRegion))
        {
            return [.. mine.SkipLast(baseRegion.Length), .. theirs];
        }
        if (_Disjoint(baseRegion, theirs, mine) is { } disjoint)
        {
            return disjoint;
        }
        overlap = true;
        return [.. mine, .. theirs];
    }

    private readonly record struct Edit(int Start, int End, string[] Lines);

    private static string[]? _Disjoint(string[] baseline, string[] theirs, string[] mine)
    {
        var theirEdits = _Edits(baseline, theirs);
        var mineEdits = _Edits(baseline, mine);
        foreach (var their in theirEdits)
        {
            foreach (var own in mineEdits)
            {
                var overlap = their.Start == their.End && own.Start == own.End ? their.Start == own.Start
                    : their.Start == their.End ? their.Start > own.Start && their.Start < own.End
                    : own.Start == own.End ? own.Start > their.Start && own.Start < their.End
                    : their.Start < own.End && own.Start < their.End;
                if (overlap)
                {
                    return null;
                }
            }
        }
        var result = new List<string>();
        var at = 0;
        foreach (var edit in theirEdits.Concat(mineEdits).OrderBy(edit => edit.Start).ThenBy(edit => edit.End))
        {
            result.AddRange(_Slice(baseline, at, edit.Start));
            result.AddRange(edit.Lines);
            at = edit.End;
        }
        result.AddRange(_Slice(baseline, at, baseline.Length));
        return result.ToArray();
    }

    private static List<Edit> _Edits(string[] baseline, string[] changed)
    {
        var result = new List<Edit>();
        int baseAt = 0, changedAt = 0;
        foreach (var (baseIndex, changedIndex) in _Match(baseline, changed).Append((baseline.Length, changed.Length)))
        {
            if (baseIndex > baseAt || changedIndex > changedAt)
            {
                result.Add(new Edit(baseAt, baseIndex, _Slice(changed, changedAt, changedIndex)));
            }
            baseAt = baseIndex + 1;
            changedAt = changedIndex + 1;
        }
        return result;
    }

    private static string[] _Slice(IReadOnlyList<string> lines, int from, int to) =>
        from >= to ? [] : [.. lines.Skip(from).Take(to - from)];

    // Longest common subsequence as increasing index pairs; the common head and tail are matched first.
    private static List<(int A, int B)> _Match(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var pairs = new List<(int A, int B)>();
        var head = 0;
        while (head < a.Count && head < b.Count && a[head] == b[head])
        {
            pairs.Add((head, head));
            head++;
        }
        var tail = 0;
        while (tail < a.Count - head && tail < b.Count - head && a[a.Count - 1 - tail] == b[b.Count - 1 - tail])
        {
            tail++;
        }
        var rows = a.Count - head - tail;
        var columns = b.Count - head - tail;
        if (rows > 0 && columns > 0 && (long)rows * columns <= MaxCells)
        {
            var table = new int[rows + 1, columns + 1];
            for (var i = rows - 1; i >= 0; i--)
            {
                for (var j = columns - 1; j >= 0; j--)
                {
                    table[i, j] = a[head + i] == b[head + j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
                }
            }
            int x = 0, y = 0;
            while (x < rows && y < columns)
            {
                if (a[head + x] == b[head + y])
                {
                    pairs.Add((head + x, head + y));
                    x++;
                    y++;
                }
                else if (table[x + 1, y] >= table[x, y + 1])
                {
                    x++;
                }
                else
                {
                    y++;
                }
            }
        }
        for (var i = tail; i > 0; i--)
        {
            pairs.Add((a.Count - i, b.Count - i));
        }
        return pairs;
    }
}
