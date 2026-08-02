namespace Cockpit.Plugin.GitHubPullRequests;

// What the side-menu badge (AC-517) shows: how many open pull requests count as "yours" (`Primary`)
// and how many of those are waiting on your review (`Secondary`) — Raymond's call over the ticket's
// own recommendation of a single "mine" count, so the two share one button ("3 / 2") instead of the badge and
// the dialog needing separate reconciling.
//
// Pure and separate from `PullRequestBadgeUpdater` so the counting rule — which pull requests count
// at all — is provable without a host, a badge, or a background fetch. Mirrors exactly what
// `GitHubPullRequestsSideSectionControl` used to compute for its own "N open · M waiting on you" line,
// so the badge does not quietly redefine what those two numbers mean.
internal static class PullRequestBadgeCounts
{
    // `Primary` counts `PullRequestFeedResult.PullRequests` with the ignored ones (by
    // url or by repository) taken out — the same set the dialog shows once its own "Show ignored" is off.
    // `Secondary` is the subset of that same, already-ignore-filtered set which also appears in
    // `PullRequestFeedResult.ReviewRequested`: a review request outside the repository filter (so
    // never in `PullRequestFeedResult.PullRequests` to begin with) does not inflate the count, the
    // same restriction the old section applied.
    public static (int Primary, int Secondary) Compute(
        PullRequestFeedResult result,
        IReadOnlySet<string> ignoredPullRequests,
        IReadOnlySet<string> ignoredRepositories)
    {
        bool IsIgnored(GitHubPullRequest pullRequest) =>
            ignoredPullRequests.Contains(pullRequest.Url) || ignoredRepositories.Contains(pullRequest.Repository);

        var reviewRequested = result.ReviewRequested.Select(pullRequest => pullRequest.Url).ToHashSet(StringComparer.Ordinal);

        var visible = result.PullRequests.Where(pullRequest => !IsIgnored(pullRequest)).ToList();
        var primary = visible.Count;
        var secondary = visible.Count(pullRequest => reviewRequested.Contains(pullRequest.Url));

        return (primary, secondary);
    }
}
