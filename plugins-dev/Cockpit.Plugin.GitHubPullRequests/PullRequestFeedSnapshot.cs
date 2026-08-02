namespace Cockpit.Plugin.GitHubPullRequests;

// What `PullRequestRefreshSource` hands every subscriber and keeps in `IPluginStorage`: the most
// recently known feed result, and when it was fetched. Staleness is not stored as a flag here — a snapshot that
// looked fresh when it was written would silently read as fresh forever after a crash or a long-idle session, which
// is exactly the lie AC-515 exists to stop telling. A view compares `FetchedAt` against
// `PullRequestRefreshSource.StaleAfter` itself, at render time, against the clock as it is then.
//
// `Result`: The feed result — open pull requests, the review-requested subset, and whether a repository is even configured.
// `FetchedAt`: When this was fetched, or `null` for `Empty` (nothing has ever loaded, not even from a previous run).
internal sealed record PullRequestFeedSnapshot(PullRequestFeedResult Result, DateTimeOffset? FetchedAt)
{
    // Before the very first load completes and nothing was persisted from a previous run — the state a brand-new
    // install starts from. An empty, not-missing result: nothing has loaded yet, which is different from "no
    // repository is configured" and must not flash that message for the instant before the first real answer lands.
    public static PullRequestFeedSnapshot Empty { get; } = new(new PullRequestFeedResult([], [], RepositoryMissing: false), FetchedAt: null);
}
