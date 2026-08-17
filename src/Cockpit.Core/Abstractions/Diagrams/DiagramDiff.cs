namespace Cockpit.Core.Abstractions.Diagrams;

// One line of a diff block, kept for context or shown as the side of a change the operator is choosing between.
public sealed record DiagramDiffLine(string Text);

// One row of an agent's proposed diagram edit (AC-825): either an unchanged run (kept verbatim regardless of any
// decision) or a change — old lines versus new lines — the operator accepts or rejects as a unit. Ordered: applying
// every block's chosen side, in this order, reconstructs exactly the source that choice implies.
public sealed record DiagramDiffBlock(bool IsChange, IReadOnlyList<DiagramDiffLine> ContextLines, IReadOnlyList<DiagramDiffLine> OldLines, IReadOnlyList<DiagramDiffLine> NewLines)
{
    public static DiagramDiffBlock Context(IReadOnlyList<string> lines) =>
        new(false, lines.Select(l => new DiagramDiffLine(l)).ToList(), [], []);

    public static DiagramDiffBlock Change(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines) =>
        new(true, [], oldLines.Select(l => new DiagramDiffLine(l)).ToList(), newLines.Select(l => new DiagramDiffLine(l)).ToList());
}

// Turns two Mermaid sources into an ordered list of diff blocks, and back into text given a per-block accept
// decision — the diff-poort's whole algorithm (AC-825). Same LCS approach as SourceChangeSummary, so a change
// is never described as bigger than it is: two lines a diagram author reordered still line up as unchanged.
public static class DiagramDiff
{
    public static IReadOnlyList<DiagramDiffBlock> Compute(string before, string after)
    {
        var a = SplitLines(before);
        var b = SplitLines(after);
        var table = BuildLcsTable(a, b);

        var ops = new List<(bool IsMatch, bool FromA, string Text)>();
        int i = a.Length, j = b.Length;
        while (i > 0 || j > 0)
        {
            if (i > 0 && j > 0 && a[i - 1] == b[j - 1])
            {
                ops.Add((true, true, a[i - 1]));
                i--; j--;
            }
            else if (j > 0 && (i == 0 || table[i, j - 1] >= table[i - 1, j]))
            {
                ops.Add((false, false, b[j - 1]));
                j--;
            }
            else
            {
                ops.Add((false, true, a[i - 1]));
                i--;
            }
        }

        ops.Reverse();
        return GroupIntoBlocks(ops);
    }

    // Applies the operator's per-block decision — true accepts a change block's new lines, false keeps its old
    // ones. Context blocks pass through untouched. `accepted` is indexed by each block's position in `blocks`; a
    // missing entry defaults to rejected, the same fail-closed default the rest of the diagram contract uses.
    public static string Apply(IReadOnlyList<DiagramDiffBlock> blocks, IReadOnlySet<int> accepted)
    {
        var lines = new List<string>();
        for (var index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            var chosen = !block.IsChange ? block.ContextLines : accepted.Contains(index) ? block.NewLines : block.OldLines;
            lines.AddRange(chosen.Select(l => l.Text));
        }

        return string.Join('\n', lines);
    }

    private static IReadOnlyList<DiagramDiffBlock> GroupIntoBlocks(List<(bool IsMatch, bool FromA, string Text)> ops)
    {
        var blocks = new List<DiagramDiffBlock>();
        var index = 0;
        while (index < ops.Count)
        {
            if (ops[index].IsMatch)
            {
                var context = new List<string>();
                while (index < ops.Count && ops[index].IsMatch)
                {
                    context.Add(ops[index].Text);
                    index++;
                }

                blocks.Add(DiagramDiffBlock.Context(context));
                continue;
            }

            var oldLines = new List<string>();
            var newLines = new List<string>();
            while (index < ops.Count && !ops[index].IsMatch)
            {
                if (ops[index].FromA)
                {
                    oldLines.Add(ops[index].Text);
                }
                else
                {
                    newLines.Add(ops[index].Text);
                }

                index++;
            }

            blocks.Add(DiagramDiffBlock.Change(oldLines, newLines));
        }

        return blocks;
    }

    private static string[] SplitLines(string text) => text.ReplaceLineEndings("\n").Split('\n');

    private static int[,] BuildLcsTable(string[] a, string[] b)
    {
        var table = new int[a.Length + 1, b.Length + 1];
        for (var x = 1; x <= a.Length; x++)
        {
            for (var y = 1; y <= b.Length; y++)
            {
                table[x, y] = a[x - 1] == b[y - 1]
                    ? table[x - 1, y - 1] + 1
                    : Math.Max(table[x - 1, y], table[x, y - 1]);
            }
        }

        return table;
    }
}
