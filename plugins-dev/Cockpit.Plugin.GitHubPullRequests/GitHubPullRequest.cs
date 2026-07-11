namespace Cockpit.Plugin.GitHubPullRequests;

/// <summary>One open GitHub pull request shown in the side section, the dialog grid, and rendered into the prompt template. <see cref="Repository"/> is the owner/name it belongs to (for the cross-repo view).</summary>
public sealed record GitHubPullRequest(int Number, string Title, string Url, string? Body, string Repository, string Author);
