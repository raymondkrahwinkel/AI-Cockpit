using System.Globalization;

namespace Cockpit.Plugin.SessionReview;

// Turns unified `git diff` text into one `FileDiff` per file (AC-578), carrying the old and new line
// number of every row. The panel used to render the diff as one flat list of coloured strings, which is why it could
// offer neither a file tree nor a line-number gutter — all the structure git had already written into the text was
// thrown away at the first `Split('\n')`. Everything the UI needs is derived here, where it can be tested
// without a window.
internal static class DiffParser
{
    // Parses a whole diff. Anything before the first `diff --git` line is ignored, so a synthesised block for an
    // untracked file can simply be appended to git's own output and arrive here as just another file.
    public static IReadOnlyList<FileDiff> Parse(string diff)
    {
        var files = new List<FileDiff>();
        if (string.IsNullOrEmpty(diff))
        {
            return files;
        }

        string? path = null;
        string? oldPath = null;
        var kind = FileChangeKind.Modified;
        var rows = new List<DiffRow>();
        int oldNumber = 0, newNumber = 0;

        void Flush()
        {
            if (path is not null)
            {
                files.Add(new FileDiff(path, kind, rows));
            }
        }

        // git's output ends with a newline, and the empty string after it is not a line of the file — left in, it
        // becomes a blank context row under the last file of every diff.
        var lines = diff.Replace("\r\n", "\n").Split('\n');
        var last = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;

        for (var index = 0; index < last; index++)
        {
            var line = lines[index];
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Flush();
                path = _PathFromGitHeader(line);
                oldPath = null;
                kind = FileChangeKind.Modified;
                rows = [];
                oldNumber = newNumber = 0;
                continue;
            }

            if (path is null)
            {
                continue; // preamble before the first file
            }

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                (oldNumber, newNumber) = ParseHunkStart(line);
                rows.Add(new DiffRow(DiffLineKind.Hunk, null, null, line));
                continue;
            }

            // Header lines only reach here before the first hunk of a file; once inside a hunk every line is content.
            if (rows.Count == 0 && _ApplyHeader(line, ref kind, ref path, ref oldPath))
            {
                continue;
            }

            switch (line.Length == 0 ? ' ' : line[0])
            {
                case '+':
                    rows.Add(new DiffRow(DiffLineKind.Added, null, newNumber++, line[1..]));
                    break;
                case '-':
                    rows.Add(new DiffRow(DiffLineKind.Removed, oldNumber++, null, line[1..]));
                    break;
                case '\\':
                    break; // "\ No newline at end of file" — a note about the line above, not a line of its own
                default:
                    // A context line is " text"; git writes a bare empty line for an empty one.
                    rows.Add(new DiffRow(DiffLineKind.Context, oldNumber++, newNumber++, line.Length == 0 ? string.Empty : line[1..]));
                    break;
            }
        }

        Flush();
        return files;
    }

    // Splits a hunk header into the range and the context git appends after the closing `@@` (usually the
    // enclosing function). The panel draws the range at the left of its separator rule and the context beside it.
    public static (string Range, string Context) SplitHunkHeader(string header)
    {
        var close = header.IndexOf("@@", 2, StringComparison.Ordinal);
        return close < 0
            ? (header.Trim(), string.Empty)
            : (header[..(close + 2)].Trim(), header[(close + 2)..].Trim());
    }

    // The first old and new line number a hunk covers, from its `@@ -a,b +c,d @@` header.
    public static (int Old, int New) ParseHunkStart(string header) =>
        (_NumberAfter(header, '-'), _NumberAfter(header, '+'));

    // The stretch that actually differs between a removed line and the added line that replaced it: the shared
    // prefix and suffix are peeled off and what remains is highlighted. This is what makes a one-character edit
    // readable — a version bump or a renamed identifier otherwise arrives as two whole red-and-green lines with
    // nothing pointing at the change.
    // Start index (shared by both), and the exclusive end index in the old and in the new text.
    public static (int Start, int OldEnd, int NewEnd) WordSpan(string removed, string added)
    {
        var start = 0;
        while (start < removed.Length && start < added.Length && removed[start] == added[start])
        {
            start++;
        }

        int oldEnd = removed.Length, newEnd = added.Length;
        while (oldEnd > start && newEnd > start && removed[oldEnd - 1] == added[newEnd - 1])
        {
            oldEnd--;
            newEnd--;
        }

        // Never cut a surrogate pair in half, or the highlight renders a replacement glyph mid-emoji.
        if (start > 0 && start < removed.Length && char.IsLowSurrogate(removed[start]))
        {
            start--;
        }

        return (start, Math.Max(start, oldEnd), Math.Max(start, newEnd));
    }

    // Whether a removed row and the row after it are an isolated replacement — the only case where a word-level
    // comparison says anything. Across a block of removals followed by a block of additions the lines do not
    // correspond one to one, and highlighting them pairwise invents a relationship that is not there.
    public static bool IsIsolatedReplacement(IReadOnlyList<DiffRow> rows, int index) =>
        index >= 0
        && index + 1 < rows.Count
        && rows[index].Kind == DiffLineKind.Removed
        && rows[index + 1].Kind == DiffLineKind.Added
        && (index == 0 || rows[index - 1].Kind != DiffLineKind.Removed)
        && (index + 2 >= rows.Count || rows[index + 2].Kind != DiffLineKind.Added);

    // Reads the file-level header lines that sit between `diff --git` and the first hunk. Returns whether the
    // line was one of them. The `+++`/`---` pair is preferred over the `diff --git` line for the path:
    // that line carries both paths separated by a space and cannot be split reliably when a name contains one.
    private static bool _ApplyHeader(string line, ref FileChangeKind kind, ref string? path, ref string? oldPath)
    {
        if (line.StartsWith("new file mode", StringComparison.Ordinal))
        {
            kind = FileChangeKind.Added;
            return true;
        }

        if (line.StartsWith("deleted file mode", StringComparison.Ordinal))
        {
            kind = FileChangeKind.Deleted;
            return true;
        }

        if (line.StartsWith("rename ", StringComparison.Ordinal) || line.StartsWith("copy ", StringComparison.Ordinal))
        {
            kind = FileChangeKind.Renamed;

            // A pure rename has no content change, so git writes no ---/+++ pair and the "rename to" line is the
            // only place the new name appears. Without this the tree files the change under the name it left.
            const string To = "rename to ";
            if (line.StartsWith(To, StringComparison.Ordinal) && line.Length > To.Length)
            {
                path = line[To.Length..].Trim();
            }

            return true;
        }

        if (line.StartsWith("Binary files ", StringComparison.Ordinal) || line.StartsWith("GIT binary patch", StringComparison.Ordinal))
        {
            kind = FileChangeKind.Binary;
            return true;
        }

        if (line.StartsWith("--- ", StringComparison.Ordinal))
        {
            oldPath = _StripPrefix(line[4..]);
            return true;
        }

        if (line.StartsWith("+++ ", StringComparison.Ordinal))
        {
            // A deletion has "+++ /dev/null"; then the old side is the only name the file has.
            path = _StripPrefix(line[4..]) ?? oldPath ?? path;
            return true;
        }

        return line.StartsWith("index ", StringComparison.Ordinal)
            || line.StartsWith("old mode", StringComparison.Ordinal)
            || line.StartsWith("new mode", StringComparison.Ordinal)
            || line.StartsWith("similarity ", StringComparison.Ordinal)
            || line.StartsWith("dissimilarity ", StringComparison.Ordinal);
    }

    // The path from a `diff --git a/x b/x` line — a fallback only, used until the `+++` line arrives and
    // for the rare diff that has none. Splits on the midpoint of the two halves, which is right whenever the two
    // names are equal and is corrected by `+++` whenever they are not.
    private static string _PathFromGitHeader(string line)
    {
        var rest = line["diff --git ".Length..].Trim();
        var half = rest.Length / 2;
        return _StripPrefix(rest[..half].Trim()) ?? rest;
    }

    // Drops git's `a/` or `b/` prefix; null for `/dev/null`, which names no file.
    private static string? _StripPrefix(string value)
    {
        var trimmed = value.Trim();
        if (trimmed is "/dev/null" or "")
        {
            return null;
        }

        return trimmed.Length > 2 && (trimmed.StartsWith("a/", StringComparison.Ordinal) || trimmed.StartsWith("b/", StringComparison.Ordinal))
            ? trimmed[2..]
            : trimmed;
    }

    // The number following the given sign in a hunk header, e.g. 16 from `@@ -16,31 +18,46 @@`.
    private static int _NumberAfter(string header, char sign)
    {
        var at = header.IndexOf(sign, 2);
        if (at < 0)
        {
            return 1;
        }

        var end = at + 1;
        while (end < header.Length && char.IsAsciiDigit(header[end]))
        {
            end++;
        }

        return end > at + 1 && int.TryParse(header.AsSpan(at + 1, end - at - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : 1;
    }
}
