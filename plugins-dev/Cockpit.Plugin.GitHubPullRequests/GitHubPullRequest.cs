namespace Cockpit.Plugin.GitHubPullRequests;

/// <summary>
/// One open GitHub pull request shown in the side section, the dialog grid, and rendered into the prompt
/// template. <see cref="Repository"/> is the owner/name it belongs to (for the cross-repo view), and
/// <see cref="UpdatedAt"/> is when it last saw any activity — a commit, a review, a comment — which is what the
/// list is ordered by: the one somebody just touched is the one worth looking at first.
/// </summary>
public sealed record GitHubPullRequest(
    int Number,
    string Title,
    string Url,
    string? Body,
    string Repository,
    string Author,
    DateTimeOffset? UpdatedAt = null);
