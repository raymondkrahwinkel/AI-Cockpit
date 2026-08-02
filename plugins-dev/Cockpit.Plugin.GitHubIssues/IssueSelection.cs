namespace Cockpit.Plugin.GitHubIssues;

// Finds the issue that survives a grid reload by identity — the same defect `YouTrackDialogControl` has
// (AC-299 bug 2), and the same fix. `GitHubIssuesDialogControl` rebuilds its grid's
// `ItemsSource` as a brand-new `ObservableCollection&lt;GitHubIssue&gt;` on every reload — Refresh, or
// the "Assigned to me" toggle. Swapping the collection instance drops the DataGrid's selection outright: it does
// not go looking through the new items for one that happens to compare equal. Matching on
// `GitHubIssue.Repository` + `GitHubIssue.Number` — the issue's identity — is what
// survives that swap. Number alone is not enough: it is only unique within a repository, and the CLI mode lists
// issues across every repo an owner has.
internal static class IssueSelection
{
    public static GitHubIssue? Restore(IEnumerable<GitHubIssue> issues, string? repository, int? number) =>
        repository is null || number is null
            ? null
            : issues.FirstOrDefault(issue =>
                issue.Number == number && string.Equals(issue.Repository, repository, StringComparison.OrdinalIgnoreCase));
}
