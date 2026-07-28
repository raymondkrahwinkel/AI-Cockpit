using System.Text.Json;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// Reads an issue's label names out of a GitHub payload. Both listings shape labels the same way — an array of objects
/// with a <c>name</c> — but one comes from <c>gh</c> and one from the REST API, and Autopilot's start gate (AC-345)
/// has to see the same labels whichever route the operator's settings take.
/// </summary>
internal static class GitHubIssueLabels
{
    /// <summary>The label names on <paramref name="issue"/>, or none when the listing did not ask for them.</summary>
    public static IReadOnlyList<string> Read(JsonElement issue)
    {
        if (!issue.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return labels.EnumerateArray()
            .Select(label => label.TryGetProperty("name", out var name) ? name.GetString() : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToList();
    }
}
