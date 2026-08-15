namespace Cockpit.Plugin.GitHubPullRequests;

// The open pull request for a session's own checked-out branch (AC-802), as returned by one `gh pr view --json
// number,headRefName,additions,deletions,url,statusCheckRollup` call — everything the session banner shows,
// collapsed and expanded. `Repository` is parsed out of `Url` rather than a seventh field, since a PR's URL
// already carries owner/name.
internal sealed record SessionPullRequestStatus(
    int Number,
    string Repository,
    string Branch,
    int Additions,
    int Deletions,
    string Url,
    IReadOnlyList<PullRequestCheck> Checks)
{
    public int ChecksTotal => Checks.Count;

    public int ChecksPassed => Checks.Count(check => check.State == PullRequestCheckState.Passed);

    public int ChecksFailed => Checks.Count(check => check.State == PullRequestCheckState.Failed);

    // Priority mirrors a dashboard's usual read: one failure marks the whole PR red even while others are still
    // running; nothing is amber-in-progress once everything has finished one way or another.
    public PullRequestCheckState OverallState =>
        ChecksFailed > 0 ? PullRequestCheckState.Failed
        : Checks.Any(check => check.State == PullRequestCheckState.Running) ? PullRequestCheckState.Running
        : ChecksTotal > 0 && ChecksPassed == ChecksTotal ? PullRequestCheckState.Passed
        : PullRequestCheckState.Other;
}
