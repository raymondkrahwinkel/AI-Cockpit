namespace Cockpit.Plugin.GitHubPullRequests;

// One pull request's live status (AC-818). `Mergeable`/`ReviewDecision` keep gh's own strings (e.g.
// "CONFLICTING"/"UNKNOWN", "APPROVED"/null) rather than a bool — those states aren't interchangeable to a caller
// deciding whether to merge.
internal sealed record GitHubPullRequestStatus(
    int Number,
    string Title,
    string Url,
    string? Mergeable,
    string? ReviewDecision,
    IReadOnlyList<PullRequestCheck> Checks);
