namespace Cockpit.Plugin.GitHubPullRequests;

// Decides which review requests are new since the last look: the pull requests waiting for your review,
// minus the ones already seen. Pure, so the "did this just arrive?" rule is tested without gh or a UI.
// The returned seen-set holds only the pull requests that are open *now*, so it cannot grow without
// bound and a request that is closed and asked for again is announced again — which is what you want.
internal static class ReviewRequestInbox
{
    public static ReviewRequestInboxResult Reconcile(IReadOnlyList<GitHubPullRequest> reviewRequested, IReadOnlySet<string> seen)
    {
        var arrived = reviewRequested.Where(pullRequest => !seen.Contains(KeyOf(pullRequest))).ToList();
        var stillOpen = reviewRequested.Select(KeyOf).ToHashSet(StringComparer.Ordinal);

        return new ReviewRequestInboxResult(arrived, stillOpen);
    }

    // Identifies a pull request across repositories — the number alone collides between repos.
    public static string KeyOf(GitHubPullRequest pullRequest) => $"{pullRequest.Repository}#{pullRequest.Number}";
}
