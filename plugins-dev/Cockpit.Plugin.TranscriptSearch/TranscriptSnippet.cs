namespace Cockpit.Plugin.TranscriptSearch;

/// <summary>
/// Builds the one-line snippet shown for a transcript search hit (#9): the matched text collapsed to a single
/// line, windowed around the first match with ellipses when it is trimmed. Pure so the windowing is testable.
/// </summary>
public static class TranscriptSnippet
{
    /// <summary>Characters of context to keep on each side of the match before adding an ellipsis.</summary>
    public const int Radius = 60;

    public static string Build(string text, string query, int radius = Radius)
    {
        var collapsed = _CollapseWhitespace(text);
        if (string.IsNullOrEmpty(query))
        {
            return _Cap(collapsed, radius * 2);
        }

        var index = collapsed.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return _Cap(collapsed, radius * 2);
        }

        var start = Math.Max(0, index - radius);
        var end = Math.Min(collapsed.Length, index + query.Length + radius);
        var window = collapsed[start..end];

        var prefix = start > 0 ? "…" : string.Empty;
        var suffix = end < collapsed.Length ? "…" : string.Empty;
        return prefix + window + suffix;
    }

    private static string _Cap(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    // Newlines and runs of whitespace become single spaces so a snippet stays on one line.
    private static string _CollapseWhitespace(string text)
    {
        var chars = new char[text.Length];
        var length = 0;
        var previousWasSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!previousWasSpace && length > 0)
                {
                    chars[length++] = ' ';
                    previousWasSpace = true;
                }
            }
            else
            {
                chars[length++] = c;
                previousWasSpace = false;
            }
        }

        return new string(chars, 0, length).TrimEnd();
    }
}
