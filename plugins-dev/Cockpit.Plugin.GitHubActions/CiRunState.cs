namespace Cockpit.Plugin.GitHubActions;

// The at-a-glance state of a workflow run, derived from GitHub's status/conclusion pair (AC-52).
internal enum CiRunState
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
