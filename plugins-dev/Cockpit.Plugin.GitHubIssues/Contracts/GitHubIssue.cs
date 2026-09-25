namespace Cockpit.Plugin.GitHubIssues;

// One open GitHub issue shown in the dialog grid and rendered into the prompt template. `Repository` is the owner/name it belongs to (for the cross-repo view).
public sealed record GitHubIssue(int Number, string Title, string Url, string? Body, string Repository)
{
    // The issue's labels — GitHub's nearest thing to a stage, which is what Autopilot's start gate keys on (AC-345).
    // Empty where the listing did not ask for them, so a caller reads "no labels" as "not known" rather than "none".
    public IReadOnlyList<string> Labels { get; init; } = [];
}
