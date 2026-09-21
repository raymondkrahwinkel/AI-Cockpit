using System.Text.RegularExpressions;

namespace Cockpit.Plugin.Autopilot;

// Pulls a ticket description's acceptance-criteria section (AC-1339): a Ready epic-sub's own sign-off, so a run
// picked off the chain is judged against it directly. Recognises a bold line (`**Acceptance criteria:**`) or a
// markdown heading (`## Acceptance criteria`); stops at the next heading of that same style, or the text's end.
internal static partial class AutopilotAcceptanceSection
{
    // No trailing anchor: a single-line field like "**Testbudget:** 4." is still heading-style for stop-detection
    // even with inline content after the closing `**` — only the leading bold span makes it one.
    [GeneratedRegex(@"^\*\*(.+?)\*\*")]
    private static partial Regex BoldHeading();

    [GeneratedRegex(@"^#{1,6}\s+(.+?)\s*$")]
    private static partial Regex MarkdownHeading();

    // The section's body, trimmed, or empty when `description` carries none of `headings` as a heading (matched
    // case-insensitively as a prefix, so "Acceptatiecriteria (tegenproef):" matches the configured
    // "Acceptatiecriteria"). `headings` is policy, not a fixed phrase — see AutopilotSettings.AcceptanceHeadings.
    public static string Extract(string description, IReadOnlyList<string> headings)
    {
        if (string.IsNullOrWhiteSpace(description) || headings.Count == 0)
        {
            return string.Empty;
        }

        var lines = description.ReplaceLineEndings("\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var heading = _MatchHeading(lines[i]);
            if (heading is not { } found || !_MatchesAny(found.Text, headings))
            {
                continue;
            }

            var body = new List<string>();
            for (var j = i + 1; j < lines.Length; j++)
            {
                if (_MatchHeading(lines[j]) is { } next && next.Bold == found.Bold)
                {
                    break;
                }

                body.Add(lines[j]);
            }

            return string.Join('\n', body).Trim();
        }

        return string.Empty;
    }

    private static bool _MatchesAny(string text, IReadOnlyList<string> headings) =>
        headings.Any(heading => text.StartsWith(heading, StringComparison.OrdinalIgnoreCase));

    private static (string Text, bool Bold)? _MatchHeading(string line)
    {
        var trimmed = line.Trim();
        if (BoldHeading().Match(trimmed) is { Success: true } bold)
        {
            return (bold.Groups[1].Value.Trim(), true);
        }

        if (MarkdownHeading().Match(trimmed) is { Success: true } markdown)
        {
            return (markdown.Groups[1].Value.Trim(), false);
        }

        return null;
    }
}
