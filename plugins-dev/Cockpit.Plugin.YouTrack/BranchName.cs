using System.Globalization;
using System.Text;

namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// Turns an issue into a branch name, following a pattern the operator sets: <c>{id}-{summary}</c> by default, but
/// <c>feature/{id}</c> or <c>{id}_{summary}</c> if that is how their team works. Everything that goes into it is
/// lowercased and made safe for git — no spaces, no punctuation git or a shell would choke on, no accents, no trailing
/// separators — because a naming convention is a preference and a broken ref is not.
/// <para>
/// The plugin only <em>offers</em> this name. Creating the branch is git's business, not YouTrack's.
/// </para>
/// </summary>
internal static class BranchName
{
    /// <summary>What the operator gets unless they say otherwise — Raymond's own convention.</summary>
    public const string DefaultPattern = "{id}-{summary}";

    // Long enough to stay recognisable, short enough to keep the ref readable in a prompt or a terminal.
    private const int MaxSummaryLength = 40;

    public static string From(string idReadable, string? summary, string? pattern = null)
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

        var template = string.IsNullOrWhiteSpace(pattern) ? DefaultPattern : pattern.Trim();

        var branch = template
            .Replace("{id}", id, StringComparison.OrdinalIgnoreCase)
            .Replace("{ticket}", id, StringComparison.OrdinalIgnoreCase)
            .Replace("{summary}", name, StringComparison.OrdinalIgnoreCase);

        // A pattern with no summary in it, or an issue with no summary, must not leave the separator dangling:
        // "EVE-14-" is a name someone typed wrong, and it looks like one.
        branch = _Tidy(branch);

        return branch.Length == 0 ? id : branch;
    }

    // Slashes survive (feature/EVE-14 is a branch), everything else that git or a shell would argue with does not.
    // Repeated or trailing separators are collapsed: a pattern is written once and used on a hundred tickets, and one
    // of them will have an empty summary.
    private static string _Tidy(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            var allowed = char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '/';
            if (!allowed)
            {
                continue;
            }

            var separator = character is '-' or '_' or '.' or '/';
            if (separator && (builder.Length == 0 || builder[^1] == character))
            {
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString().Trim('-', '_', '.', '/');
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
