namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// The prompt dropped into the active session when an issue is clicked. English by default (the cockpit's
/// UI language) and editable in the plugin's Options tab; placeholders are substituted per issue:
/// <c>{number}</c>, <c>{title}</c>, <c>{url}</c>, <c>{owner}</c>, <c>{repo}</c>, <c>{body}</c>.
/// </summary>
internal static class PromptTemplate
{
    public const string Default =
        "Please open and review GitHub issue #{number} (\"{title}\") from {owner}/{repo}.\n" +
        "Link: {url}\n\n" +
        "Issue description:\n{body}\n\n" +
        "Read the issue and its context, then help me address it.";

    public static string Render(string template, GitHubIssue issue, string owner, string repo) =>
        template
            .Replace("{number}", issue.Number.ToString())
            .Replace("{title}", issue.Title)
            .Replace("{url}", issue.Url)
            .Replace("{owner}", owner)
            .Replace("{repo}", repo)
            .Replace("{body}", string.IsNullOrWhiteSpace(issue.Body) ? "(no description)" : issue.Body);
}
