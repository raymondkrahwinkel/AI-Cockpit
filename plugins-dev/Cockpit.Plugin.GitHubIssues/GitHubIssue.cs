namespace Cockpit.Plugin.GitHubIssues;

/// <summary>One open GitHub issue shown in the dialog grid and rendered into the prompt template. <see cref="Repository"/> is the owner/name it belongs to (for the cross-repo view).</summary>
public sealed record GitHubIssue(int Number, string Title, string Url, string? Body, string Repository)
{
    /// <summary>
    /// The issue's labels — GitHub's nearest thing to a stage, which is what Autopilot's start gate keys on (AC-345).
    /// Empty where the listing did not ask for them, so a caller reads "no labels" as "not known" rather than "none".
    /// </summary>
    public IReadOnlyList<string> Labels { get; init; } = [];
}
