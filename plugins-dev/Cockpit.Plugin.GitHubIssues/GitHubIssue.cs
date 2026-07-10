namespace Cockpit.Plugin.GitHubIssues;

/// <summary>One open GitHub issue as shown in the left-menu list and rendered into the prompt template.</summary>
public sealed record GitHubIssue(int Number, string Title, string Url, string? Body);
