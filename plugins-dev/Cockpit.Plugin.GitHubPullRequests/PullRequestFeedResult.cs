namespace Cockpit.Plugin.GitHubPullRequests;

// One load of the pull-request feed (`PullRequestFeed`): the open pull requests, ordered and
// repository-filtered, plus the subset that is waiting on *your* review — kept separate because a
// review request is styled, counted separately in the side-menu badge (AC-517), and announced, not just counted.
//
// `PullRequests`: Open pull requests, newest activity first, after the optional repository filter.
// `ReviewRequested`: The open pull requests awaiting your review (empty in single-repo HTTP mode, which has no such search).
// `RepositoryMissing`:
// True when the GitHub CLI is off and no owner/repo is set — there is nothing to query, and the caller shows
// "open the settings" rather than an empty list that reads as "no open pull requests".
internal sealed record PullRequestFeedResult(
    IReadOnlyList<GitHubPullRequest> PullRequests,
    IReadOnlyList<GitHubPullRequest> ReviewRequested,
    bool RepositoryMissing)
{
    // The HTTP-mode-with-no-repository outcome: nothing loaded, and the flag that says why.
    public static PullRequestFeedResult Missing { get; } = new([], [], RepositoryMissing: true);
}
