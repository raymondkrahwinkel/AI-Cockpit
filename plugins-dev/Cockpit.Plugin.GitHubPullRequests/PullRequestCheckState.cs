namespace Cockpit.Plugin.GitHubPullRequests;

// The at-a-glance state of one status check on a pull request (AC-802), derived from `gh pr view`'s
// `statusCheckRollup` — same four buckets as Cockpit.Plugin.GitHubActions.CiRunState, so the session banner and
// the header's CI icon speak one CI language.
internal enum PullRequestCheckState
{
    // Queued or in progress — not finished yet (amber).
    Running,

    // Completed successfully (green).
    Passed,

    // Completed with a failure / timed out / startup failure (red).
    Failed,

    // Completed but neither pass nor fail — cancelled, skipped, neutral (grey).
    Other,
}
