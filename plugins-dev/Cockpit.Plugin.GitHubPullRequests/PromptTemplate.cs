namespace Cockpit.Plugin.GitHubPullRequests;

// The prompt dropped into the active session (or the clipboard, with no active session) when a pull
// request is clicked. English by default (the cockpit's UI language) and editable in the plugin's
// settings; placeholders are substituted per pull request: `{number}`, `{title}`, `{url}`,
// `{owner}`, `{repo}`, `{body}`, `{author}`.
internal static class PromptTemplate
{
    public const string Default =
        "Please open and review GitHub pull request #{number} (\"{title}\") from {owner}/{repo}, opened by {author}.\n" +
        "Link: {url}\n\n" +
        "Pull request description:\n{body}\n\n" +
        "Read the PR and its diff, then help me review it.";

    public static string Render(string template, GitHubPullRequest pullRequest, string owner, string repo) =>
        template
            .Replace("{number}", pullRequest.Number.ToString())
            .Replace("{title}", pullRequest.Title)
            .Replace("{url}", pullRequest.Url)
            .Replace("{owner}", owner)
            .Replace("{repo}", repo)
            .Replace("{author}", string.IsNullOrWhiteSpace(pullRequest.Author) ? "(unknown)" : pullRequest.Author)
            .Replace("{body}", string.IsNullOrWhiteSpace(pullRequest.Body) ? "(no description)" : pullRequest.Body);
}
