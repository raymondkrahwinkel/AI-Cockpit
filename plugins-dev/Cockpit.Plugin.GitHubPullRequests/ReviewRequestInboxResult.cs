namespace Cockpit.Plugin.GitHubPullRequests;

// The outcome of `ReviewRequestInbox.Reconcile`: the review requests that arrived since the last
// look (announce these), and the seen-set to persist for the next one.
internal sealed record ReviewRequestInboxResult(IReadOnlyList<GitHubPullRequest> Arrived, IReadOnlySet<string> Seen);
