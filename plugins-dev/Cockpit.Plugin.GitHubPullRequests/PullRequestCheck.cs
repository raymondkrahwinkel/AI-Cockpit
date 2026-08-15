namespace Cockpit.Plugin.GitHubPullRequests;

// One entry from a pull request's `statusCheckRollup` (AC-802) — a GitHub Actions check run or a legacy commit
// status, normalised to the same three-field shape the banner's expanded list shows: name, state, how long it ran.
internal sealed record PullRequestCheck(string Name, PullRequestCheckState State, TimeSpan? Duration);
