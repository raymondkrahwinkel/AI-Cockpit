namespace Cockpit.Plugin.GitHubPullRequests;

/// <summary>
/// The outcome of <see cref="ReviewRequestInbox.Reconcile"/>: the review requests that arrived since the last
/// look (announce these), and the seen-set to persist for the next one.
/// </summary>
internal sealed record ReviewRequestInboxResult(IReadOnlyList<GitHubPullRequest> Arrived, IReadOnlySet<string> Seen);
