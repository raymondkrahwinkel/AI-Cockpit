namespace Cockpit.Core.Abstractions.Diagrams;

// AC-810: what a whole-source edit's consent prompt says about the change, derived mechanically rather than from
// agent prose. Counts the longest shared run of lines (an LCS), not a line-count delta that would call an in-place
// rewrite "unchanged"; shared by every text-carrying collab surface (AC-872), so it names no format of its own.
public static class SourceChangeSummary
{
    public static string Describe(string before, string after)
    {
        if (string.IsNullOrWhiteSpace(before))
        {
            var lines = SplitLines(after);
            return $"written for the first time ({lines.Length} line{(lines.Length == 1 ? "" : "s")})";
        }

        var beforeLines = SplitLines(before);
        var afterLines = SplitLines(after);
        var shared = LongestCommonSubsequenceLength(beforeLines, afterLines);
        var removed = beforeLines.Length - shared;
        var added = afterLines.Length - shared;

        if (added == 0 && removed == 0)
        {
            return "no textual change";
        }

        if (removed == 0)
        {
            return $"{added} line{(added == 1 ? "" : "s")} added";
        }

        if (added == 0)
        {
            return $"{removed} line{(removed == 1 ? "" : "s")} removed";
        }

        return $"{added} line{(added == 1 ? "" : "s")} added, {removed} line{(removed == 1 ? "" : "s")} removed";
    }

    private static string[] SplitLines(string text) => text.ReplaceLineEndings("\n").Split('\n');

    private static int LongestCommonSubsequenceLength(string[] a, string[] b)
    {
        var table = new int[a.Length + 1, b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                table[i, j] = a[i - 1] == b[j - 1]
                    ? table[i - 1, j - 1] + 1
                    : Math.Max(table[i - 1, j], table[i, j - 1]);
            }
        }

        return table[a.Length, b.Length];
    }
}
