namespace Cockpit.Plugin.GitHubPullRequests;

// One open GitHub pull request shown in the dashboard widget, the dialog grid, and rendered into the prompt
// template. `Repository` is the owner/name it belongs to (for the cross-repo view), and
// `UpdatedAt` is when it last saw any activity — a commit, a review, a comment — which is what the
// list is ordered by: the one somebody just touched is the one worth looking at first.
public sealed record GitHubPullRequest(
    int Number,
    string Title,
    string Url,
    string? Body,
    string Repository,
    string Author,
    DateTimeOffset? UpdatedAt = null)
{
    // Display-only: ordering/caching compare `UpdatedAt` itself, which is offset-agnostic already.
    public DateTimeOffset? UpdatedAtLocal => UpdatedAt?.ToLocalTime();
}
