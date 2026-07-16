namespace Cockpit.Plugin.GitHubPullRequests;

/// <summary>
/// The fetch behind both surfaces that list open pull requests — the always-on side-menu section and the
/// dashboard widget (#AC-18). It answers one question, "which pull requests are open right now", the same way
/// for both: the CLI mode's three searches (yours, watched repositories, everything you are involved with)
/// merged by url with the review-requested ones, or the single repository the HTTP mode talks to; then the
/// optional repository filter, then newest-activity-first.
/// <para>
/// Pulled out of the side section so the widget is a second <em>view</em> of the same data, not a second copy
/// of the query — the merge rules, the "@me spans owners" subtlety and the ordering live in one place. What is
/// surface-specific — toasts for newly-arrived review requests, the cached last list, the ignored-PR curation —
/// stays with each surface; this returns the facts, not the presentation.
/// </para>
/// </summary>
internal sealed class PullRequestFeed
{
    private readonly GitHubPullRequestsClient _http = new();
    private readonly GitHubPrGhClient _gh = new();

    public async Task<PullRequestFeedResult> LoadAsync(GitHubPullRequestsSettings settings, bool forceRefresh, CancellationToken cancellationToken)
    {
        IReadOnlyList<GitHubPullRequest> reviewRequested = [];
        IReadOnlyList<GitHubPullRequest> all;

        if (settings.UseGitHubCli)
        {
            reviewRequested = await _gh.SearchReviewRequestedAsync(forceRefresh, cancellationToken);

            var open = await _gh.SearchOpenPullRequestsAsync(settings.GhOwner, assignedToMe: false, forceRefresh, cancellationToken);

            // Everything open in the repositories the operator watches, whoever opened it. The searches above all
            // ask "which of these are mine", which is the wrong question for a project you are responsible for:
            // five open pull requests in a repo of yours, none of them yours, showed nothing at all.
            var watched = new List<GitHubPullRequest>();
            if (settings.WatchEverythingIAmInvolvedWith)
            {
                watched.AddRange(await _gh.SearchInvolvedAsync(forceRefresh, cancellationToken));
            }

            foreach (var scope in settings.WatchedReposList)
            {
                watched.AddRange(await _gh.SearchWatchedAsync(scope, forceRefresh, cancellationToken));
            }

            // One list: a review request is an open pull request that happens to be waiting on you, and the search
            // that finds it is a different query — not a different kind of thing. Merged by url so a PR that is
            // found by two of the three searches does not appear twice.
            var seen = new HashSet<string>(reviewRequested.Select(pullRequest => pullRequest.Url), StringComparer.Ordinal);
            all = reviewRequested
                .Concat(open.Concat(watched).Where(pullRequest => seen.Add(pullRequest.Url)))
                .ToList();
        }
        else
        {
            // The HTTP mode talks to one repository; with none set there is nothing to ask.
            if (string.IsNullOrWhiteSpace(settings.Owner) || string.IsNullOrWhiteSpace(settings.Repo))
            {
                return PullRequestFeedResult.Missing;
            }

            all = await _http.GetOpenPullRequestsAsync(settings.Owner, settings.Repo, settings.Token, assignedToMe: false, cancellationToken);
        }

        // Optional repository filter: when set, keep only PRs in the chosen owner/repo list.
        var filter = settings.RepoFilterSet;
        var visible = filter.Count == 0 ? all : all.Where(pullRequest => filter.Contains(pullRequest.Repository));

        // Newest activity on top — a commit, a review, a comment. The list is short, so the question it has to
        // answer is "what moved", not "what exists". One without a date sorts last rather than to the top, which
        // is what DateTimeOffset.MinValue does for a null.
        var ordered = visible
            .OrderByDescending(pullRequest => pullRequest.UpdatedAt ?? DateTimeOffset.MinValue)
            .ToList();

        return new PullRequestFeedResult(ordered, reviewRequested, RepositoryMissing: false);
    }
}
