using System.Text;

namespace Cockpit.Plugin.Kubernetes.Helm;

// A unified line diff between two versions of one manifest document (AC-1061 fase 2). This is what the operator
// reads before approving a rollback, so it must never describe a change as bigger or smaller than it is: the shared
// head and tail are trimmed first and the rest goes through an LCS, the same approach the diagram diff uses.
internal static class ManifestLineDiff
{
    // Lines of unchanged text kept around each change, so a diff reads in place instead of as bare lines.
    private const int ContextLines = 3;

    // ponytail: the LCS table is O(old x new), so a pair of very large documents (a bundled dashboard ConfigMap) is
    // reported as a whole-document replacement instead. Raise or switch to a linear-space Myers if that ever bites.
    private const int MaxLcsLines = 800;

    public static (string Text, int Added, int Removed) Compute(string before, string after)
    {
        var a = _SplitLines(before);
        var b = _SplitLines(after);

        var head = 0;
        while (head < a.Length && head < b.Length && a[head] == b[head])
        {
            head++;
        }

        var tail = 0;
        while (tail < a.Length - head && tail < b.Length - head && a[^(tail + 1)] == b[^(tail + 1)])
        {
            tail++;
        }

        var oldMiddle = a[head..(a.Length - tail)];
        var newMiddle = b[head..(b.Length - tail)];
        List<(char Marker, string Text)> rows;
        if (oldMiddle.Length > MaxLcsLines || newMiddle.Length > MaxLcsLines)
        {
            rows = [.. oldMiddle.Select(line => ('-', line)), .. newMiddle.Select(line => ('+', line))];
        }
        else
        {
            rows = _Align(oldMiddle, newMiddle);
        }

        return _Render(a, head, tail, rows);
    }

    private static List<(char Marker, string Text)> _Align(string[] a, string[] b)
    {
        var table = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                table[i, j] = a[i] == b[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        var rows = new List<(char, string)>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y])
            {
                rows.Add((' ', a[x]));
                x++;
                y++;
            }
            else if (table[x + 1, y] >= table[x, y + 1])
            {
                rows.Add(('-', a[x]));
                x++;
            }
            else
            {
                rows.Add(('+', b[y]));
                y++;
            }
        }

        rows.AddRange(a[x..].Select(line => ('-', line)));
        rows.AddRange(b[y..].Select(line => ('+', line)));
        return rows;
    }

    // Puts the trimmed head and tail back as context, keeping at most ContextLines of it on either side of the
    // changed middle so a three-line change in a two-hundred-line document reads as three lines.
    private static (string Text, int Added, int Removed) _Render(string[] a, int head, int tail, List<(char Marker, string Text)> middle)
    {
        var rows = new List<(char Marker, string Text)>();
        var headContext = Math.Min(head, ContextLines);
        rows.AddRange(a[(head - headContext)..head].Select(line => (' ', line)));
        rows.AddRange(middle);
        var tailStart = a.Length - tail;
        rows.AddRange(a[tailStart..Math.Min(a.Length, tailStart + Math.Min(tail, ContextLines))].Select(line => (' ', line)));

        var builder = new StringBuilder();
        int added = 0, removed = 0;
        foreach (var (marker, text) in rows)
        {
            if (marker == '+')
            {
                added++;
            }
            else if (marker == '-')
            {
                removed++;
            }

            builder.Append(marker).Append(text).Append('\n');
        }

        return (builder.ToString().TrimEnd('\n'), added, removed);
    }

    private static string[] _SplitLines(string text) =>
        text.Length == 0 ? [] : text.Replace("\r\n", "\n").Split('\n');
}
