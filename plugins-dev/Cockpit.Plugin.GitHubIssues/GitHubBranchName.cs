using System.Globalization;
using System.Text;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// The branch name for an issue (#77) — <c>42-fix-the-login-redirect</c>. The step that starts an issue is the only
/// one that knows both its number and its title, so it hands the name on and the flow does not have to build it out of
/// two fields and a guess about punctuation.
/// <para>
/// Git-safe by construction: lowercase, no spaces, no punctuation a shell or a ref would argue with, and no trailing
/// dot or dash.
/// </para>
/// </summary>
internal static class GitHubBranchName
{
    /// <summary>What the operator gets unless they say otherwise.</summary>
    public const string DefaultPattern = "{number}-{title}";

    private const int MaxWords = 60;

    public static string From(int number, string? title, string? pattern = null)
    {
        var slug = new StringBuilder();

        // Decomposed first, so an accent becomes a mark that can be dropped: "ümlauts" is a branch called "umlauts",
        // not "mlauts", and not a ref you have to copy-paste because you cannot type it. Same rule as the YouTrack
        // plugin's branch names — one convention, not two.
        foreach (var character in (title ?? string.Empty).Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsAsciiLetterOrDigit(character))
            {
                slug.Append(char.ToLowerInvariant(character));
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        var words = slug.ToString().Trim('-');
        if (words.Length > MaxWords)
        {
            words = words[..MaxWords].TrimEnd('-');
        }

        var template = string.IsNullOrWhiteSpace(pattern) ? DefaultPattern : pattern.Trim();

        var branch = template
            .Replace("{number}", number.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{issue}", number.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{title}", words, StringComparison.OrdinalIgnoreCase)
            .Replace("{summary}", words, StringComparison.OrdinalIgnoreCase);

        // A pattern with no title in it, or an issue whose title slugs to nothing, must not leave the separator
        // dangling: "42-" is a name someone typed wrong, and it looks like one.
        branch = _Tidy(branch);

        return branch.Length == 0 ? number.ToString() : branch;
    }

    // Slashes survive (feature/42 is a branch); everything git or a shell would argue with does not, and repeated or
    // trailing separators are collapsed — a pattern is written once and used on a hundred issues, and one of them will
    // have a title made entirely of punctuation.
    private static string _Tidy(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.' or '/'))
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
}
