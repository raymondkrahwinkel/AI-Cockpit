namespace Cockpit.Plugin.GitHubActions;

/// <summary>One GitHub Actions workflow run (AC-52), as returned by <c>gh run list --json …</c>.</summary>
internal sealed record CiRun(
    string WorkflowName,
    string Branch,
    string Event,
    string Status,
    string Conclusion,
    DateTimeOffset? CreatedAt,
    string Url)
{
    /// <summary>The at-a-glance state, from GitHub's status (queued/in_progress/completed) and conclusion.</summary>
    public CiRunState State =>
        !string.Equals(Status, "completed", StringComparison.OrdinalIgnoreCase)
            ? CiRunState.Running
            : Conclusion.ToLowerInvariant() switch
            {
                "success" => CiRunState.Passed,
                "failure" or "timed_out" or "startup_failure" => CiRunState.Failed,
                _ => CiRunState.Other,
            };
}
