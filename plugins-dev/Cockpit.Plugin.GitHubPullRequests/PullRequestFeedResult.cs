namespace Cockpit.Plugin.GitHubPullRequests;

/// <summary>
/// One load of the pull-request feed (<see cref="PullRequestFeed"/>): the open pull requests, ordered and
/// repository-filtered, plus the subset that is waiting on <em>your</em> review — kept separate because a
/// review request is styled and (in the side section) announced, not just counted.
/// </summary>
/// <param name="PullRequests">Open pull requests, newest activity first, after the optional repository filter.</param>
/// <param name="ReviewRequested">The open pull requests awaiting your review (empty in single-repo HTTP mode, which has no such search).</param>
/// <param name="RepositoryMissing">
/// True when the GitHub CLI is off and no owner/repo is set — there is nothing to query, and the caller shows
/// "open the settings" rather than an empty list that reads as "no open pull requests".
/// </param>
internal sealed record PullRequestFeedResult(
    IReadOnlyList<GitHubPullRequest> PullRequests,
    IReadOnlyList<GitHubPullRequest> ReviewRequested,
    bool RepositoryMissing)
{
    /// <summary>The HTTP-mode-with-no-repository outcome: nothing loaded, and the flag that says why.</summary>
    public static PullRequestFeedResult Missing { get; } = new([], [], RepositoryMissing: true);
}
