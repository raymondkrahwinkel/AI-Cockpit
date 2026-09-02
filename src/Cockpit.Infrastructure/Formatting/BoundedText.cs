namespace Cockpit.Infrastructure.Formatting;

// Cutting text to a length without cutting through a character. One implementation, since it's used in more
// than one place (audit trails, the agent roster) and a second copy is a second chance to get it wrong.
internal static class BoundedText
{
    // The cap is for the complete reply, including the warning. A preview only contains complete lines: a partial
    // JSON/log line looks complete enough for an agent to build on, which is worse than showing no preview at all.
    internal static string ToolResult(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        const int headerReserve = 512;
        var preview = _CompleteLines(text, Math.Max(0, maxLength - headerReserve), out var shownLines);
        var totalLines = text.Count(character => character == '\n') + 1;
        var header = $"Tool result truncated by Cockpit.\n"
            + $"Original result: {text.Length} chars across {totalLines} lines.\n"
            + $"Shown: first {preview.Length} chars across {shownLines} complete lines.\n"
            + $"Omitted: {text.Length - preview.Length} chars and {totalLines - shownLines} lines.\n"
            + "This result is incomplete. Refine or paginate the tool call; do not assume omitted data.\n"
            + "--- Preview ---\n";

        return header + preview;
    }

    // Trims `text` to `maxLength` characters plus an ellipsis, or returns it unchanged if it already fits.
    // Surrogate-safe (C5): an astral character straddling the limit is not cut through, which would
    // otherwise leave a lone surrogate persisted or serialised as U+FFFD.
    internal static string Trim(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        var cut = char.IsHighSurrogate(text[maxLength - 1]) ? maxLength - 1 : maxLength;
        return text[..cut] + "…";
    }

    private static string _CompleteLines(string text, int maxLength, out int lineCount)
    {
        var end = 0;
        lineCount = 0;
        while (end < text.Length)
        {
            var newline = text.IndexOf('\n', end);
            var next = newline < 0 ? text.Length : newline + 1;
            if (next > maxLength)
            {
                break;
            }

            end = next;
            lineCount++;
        }

        return text[..end];
    }
}
