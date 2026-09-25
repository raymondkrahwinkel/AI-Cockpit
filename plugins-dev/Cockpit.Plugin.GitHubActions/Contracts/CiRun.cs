namespace Cockpit.Plugin.GitHubActions.Contracts;

// One GitHub Actions workflow run (AC-52/AC-1065), as returned by `gh run list --json …`. UpdatedAt is optional
// (defaults null) so the existing positional construction in tests and ParseRuns keeps compiling unchanged.
// AC-1394: shared between the backend and UI parts as linked source (see GitHubActionsChannel.cs), since the
// backend answers this type over the plugin's channel and the UI renders it.
internal sealed record CiRun(
    string WorkflowName,
    string Branch,
    string Event,
    string Status,
    string Conclusion,
    DateTimeOffset? CreatedAt,
    string Url,
    DateTimeOffset? UpdatedAt = null)
{
    // The at-a-glance state, from GitHub's status (queued/in_progress/completed) and conclusion.
    public CiRunState State =>
        !string.Equals(Status, "completed", StringComparison.OrdinalIgnoreCase)
            ? CiRunState.Running
            : Conclusion.ToLowerInvariant() switch
            {
                "success" => CiRunState.Passed,
                "failure" or "timed_out" or "startup_failure" => CiRunState.Failed,
                _ => CiRunState.Other,
            };

    // How long the run has taken so far, from queued to its last known update — null until both timestamps are known.
    public TimeSpan? Duration =>
        CreatedAt is { } createdAt && UpdatedAt is { } updatedAt && updatedAt > createdAt
            ? updatedAt - createdAt
            : null;
}
