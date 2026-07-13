using System.Globalization;
using System.Text;

namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// Turns an issue into the branch name Raymond works by: <c>[issue-id]-[short-name]</c>, lowercased and safe
/// for git (no spaces, no punctuation git or a shell would choke on, no accents, no trailing separators).
/// The plugin only <em>offers</em> this name — creating the branch is git's business, not YouTrack's.
/// </summary>
internal static class BranchName
{
    // Long enough to stay recognisable, short enough to keep the ref readable in a prompt or a terminal.
    private const int MaxSummaryLength = 40;

    public static string From(string idReadable, string? summary)
    {
        var id = _Slug(idReadable);
        var name = _Slug(summary);
        if (name.Length > MaxSummaryLength)
        {
            // Cut on a word boundary when there is one, so the name does not end mid-word.
            name = name[..MaxSummaryLength];
            var lastSeparator = name.LastIndexOf('-');
            if (lastSeparator > 0)
            {
                name = name[..lastSeparator];
            }
        }

        return name.Length == 0 ? id : $"{id}-{name}";
    }

    // Accents folded to their base letter (naïve -> naive), everything that is not a letter or digit collapsed
    // to a single separator: git accepts more than this, but a branch name is also something you type and read.
    private static string _Slug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);

        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        return builder.ToString().Trim('-');
    }
}
