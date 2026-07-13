using System.Text.RegularExpressions;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// Which issue, in which repository (#77). A flow refers to an issue the way a person writes one down, and people
/// write it down in three ways: <c>42</c> with the repo in its own field, <c>owner/repo#42</c> in one, or the URL
/// they copied from the browser. All three mean the same issue, and refusing two of them would be pedantry.
/// </summary>
/// <param name="Repository">Owner and repo, as <c>owner/repo</c>.</param>
/// <param name="Number">The issue number.</param>
public sealed partial record GitHubIssueReference(string Repository, int Number)
{
    /// <summary>Reads what the operator wrote. Throws with the three forms it accepts, rather than guessing at a fourth.</summary>
    public static GitHubIssueReference Parse(string issue, string repository)
    {
        var text = issue.Trim();
        var repo = repository.Trim();

        if (text.Length == 0)
        {
            throw new InvalidOperationException("This step has no issue. Write a number (42), an owner/repo#number, or paste the issue's URL.");
        }

        if (Url().Match(text) is { Success: true } url)
        {
            return new GitHubIssueReference(url.Groups[1].Value, int.Parse(url.Groups[2].Value));
        }

        if (Qualified().Match(text) is { Success: true } qualified)
        {
            return new GitHubIssueReference(qualified.Groups[1].Value, int.Parse(qualified.Groups[2].Value));
        }

        var bare = text.TrimStart('#');
        if (!int.TryParse(bare, out var number))
        {
            throw new InvalidOperationException($"'{issue}' is not an issue. Write a number (42), an owner/repo#number, or paste the issue's URL.");
        }

        if (repo.Length == 0 || !repo.Contains('/'))
        {
            // A bare number with no repository names an issue in a repository nobody stated. Commenting on the wrong
            // repo's #42 is not a mistake that announces itself.
            throw new InvalidOperationException($"Issue {number} in which repository? Fill in Repository as owner/repo, or write the issue as owner/repo#{number}.");
        }

        return new GitHubIssueReference(repo, number);
    }

    [GeneratedRegex(@"^https?://github\.com/([^/\s]+/[^/\s]+)/issues/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex(@"^([^/\s]+/[^/\s#]+)#?(\d+)$")]
    private static partial Regex Qualified();
}
