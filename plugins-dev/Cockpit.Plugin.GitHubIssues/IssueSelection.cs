namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// Finds the issue that survives a grid reload by identity — the same defect <c>YouTrackDialogControl</c> has
/// (AC-299 bug 2), and the same fix. <see cref="GitHubIssuesDialogControl"/> rebuilds its grid's
/// <c>ItemsSource</c> as a brand-new <c>ObservableCollection&lt;GitHubIssue&gt;</c> on every reload — Refresh, or
/// the "Assigned to me" toggle. Swapping the collection instance drops the DataGrid's selection outright: it does
/// not go looking through the new items for one that happens to compare equal. Matching on
/// <see cref="GitHubIssue.Repository"/> + <see cref="GitHubIssue.Number"/> — the issue's identity — is what
/// survives that swap. Number alone is not enough: it is only unique within a repository, and the CLI mode lists
/// issues across every repo an owner has.
/// </summary>
internal static class IssueSelection
{
    public static GitHubIssue? Restore(IEnumerable<GitHubIssue> issues, string? repository, int? number) =>
        repository is null || number is null
            ? null
            : issues.FirstOrDefault(issue =>
                issue.Number == number && string.Equals(issue.Repository, repository, StringComparison.OrdinalIgnoreCase));
}
