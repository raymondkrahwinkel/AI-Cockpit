namespace Cockpit.Plugin.GitHubPullRequests;

// One pull request's live status (AC-818): title, checks, and the two merge-readiness signals `gh pr view`
// reports as their own GitHub vocabulary — `Mergeable` is "MERGEABLE"/"CONFLICTING"/"UNKNOWN", `ReviewDecision`
// is "APPROVED"/"CHANGES_REQUESTED"/"REVIEW_REQUIRED" or null when no review is required. Kept as GitHub's own
// strings rather than collapsed into a bool: "UNKNOWN" (GitHub still computing mergeability) is not the same
// answer as "CONFLICTING", and a caller deciding whether to merge needs to tell them apart.
internal sealed record GitHubPullRequestStatus(
    int Number,
    string Title,
    string Url,
    string? Mergeable,
    string? ReviewDecision,
    IReadOnlyList<PullRequestCheck> Checks);
