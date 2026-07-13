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
    private const int MaxWords = 60;

    public static string From(int number, string? title)
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

        return words.Length == 0 ? number.ToString() : $"{number}-{words}";
    }
}
