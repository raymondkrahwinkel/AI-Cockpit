namespace Cockpit.Plugin.GitHubActions;

/// <summary>The at-a-glance state of a workflow run, derived from GitHub's status/conclusion pair (AC-52).</summary>
internal enum CiRunState
{
    /// <summary>Queued or in progress — not finished yet (amber).</summary>
    Running,

    /// <summary>Completed successfully (green).</summary>
    Passed,

    /// <summary>Completed with a failure / timed out / startup failure (red).</summary>
    Failed,

    /// <summary>Completed but neither pass nor fail — cancelled, skipped, neutral (grey).</summary>
    Other,
}
