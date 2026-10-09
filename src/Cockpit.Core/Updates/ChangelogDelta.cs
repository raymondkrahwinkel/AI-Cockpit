using System.Text;
using System.Text.RegularExpressions;

namespace Cockpit.Core.Updates;

// What an update brings, read off two copies of CHANGELOG.md (AC-1515): the one at the build you run and the one at
// the build on offer. A nightly's entries never get a version heading of their own, so "after mine, up to theirs"
// is every bullet the offered copy has that yours does not — grouped under the section it sits in there.
public static partial class ChangelogDelta
{
    public static string Between(string current, string offered)
    {
        var known = _Items(current).Select(item => item.Key).ToHashSet(StringComparer.Ordinal);

        var markdown = new StringBuilder();
        foreach (var section in _Items(offered).Where(item => !known.Contains(item.Key)).GroupBy(item => item.Section))
        {
            if (section.Key.Length > 0)
            {
                markdown.Append(section.Key).Append("\n\n");
            }

            foreach (var item in section)
            {
                markdown.Append(item.Text).Append('\n');
            }

            markdown.Append('\n');
        }

        return markdown.ToString().TrimEnd();
    }

    // Every top-level bullet with its wrapped continuation lines, and the `###` heading it falls under. The key
    // collapses whitespace, so re-wrapping an entry between two builds does not make it read as new.
    private static List<(string Section, string Text, string Key)> _Items(string changelog)
    {
        var items = new List<(string Section, string Text, string Key)>();
        var section = string.Empty;
        StringBuilder? bullet = null;

        foreach (var line in changelog.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            if (bullet is not null && line.Length > 0 && char.IsWhiteSpace(line[0]) && line.Trim().Length > 0)
            {
                bullet.Append('\n').Append(line);
                continue;
            }

            Flush();

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                section = string.Empty;
            }
            else if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                section = line.TrimEnd();
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                bullet = new StringBuilder(line);
            }
        }

        Flush();

        return items;

        void Flush()
        {
            if (bullet is not null)
            {
                var text = bullet.ToString().TrimEnd();
                items.Add((section, text, Whitespace().Replace(text, " ")));
                bullet = null;
            }
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
