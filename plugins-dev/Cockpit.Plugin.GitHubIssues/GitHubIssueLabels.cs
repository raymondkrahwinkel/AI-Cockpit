using System.Text.Json;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// Reads label names out of a GitHub payload — an issue's own labels, or a repo's whole label list (AC-519). Both
/// listings shape a label the same way — an array of objects with a <c>name</c> — but one comes from <c>gh</c> and
/// one from the REST API, and Autopilot's start gate (AC-345) has to see the same labels whichever route the
/// operator's settings take.
/// </summary>
internal static class GitHubIssueLabels
{
    /// <summary>The label names on <paramref name="issue"/>, or none when the listing did not ask for them.</summary>
    public static IReadOnlyList<string> Read(JsonElement issue) =>
        issue.TryGetProperty("labels", out var labels) ? ReadListing(labels) : [];

    /// <summary>
    /// The label names in a raw label-listing response — <c>gh label list --json name</c> or the REST
    /// <c>GET /repos/{owner}/{repo}/labels</c> — which shapes each entry the same way an issue's own labels array
    /// does (an object with a <c>name</c>). <see cref="Read"/> delegates here rather than the other way round, so a
    /// repo's label list (AC-519) goes through the same, and only, normalization as an issue's labels.
    /// </summary>
    public static IReadOnlyList<string> ReadListing(JsonElement labels)
    {
        if (labels.ValueKind != JsonValueKind.Array)
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
