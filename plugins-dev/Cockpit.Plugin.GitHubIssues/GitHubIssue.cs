namespace Cockpit.Plugin.GitHubIssues;

/// <summary>One open GitHub issue shown in the dialog grid and rendered into the prompt template. <see cref="Repository"/> is the owner/name it belongs to (for the cross-repo view).</summary>
public sealed record GitHubIssue(int Number, string Title, string Url, string? Body, string Repository);
