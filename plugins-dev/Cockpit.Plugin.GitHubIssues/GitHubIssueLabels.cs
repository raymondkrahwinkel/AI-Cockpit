using System.Text.Json;

namespace Cockpit.Plugin.GitHubIssues;

// Reads label names out of a GitHub payload — an issue's own labels, or a repo's whole label list (AC-519). Both
// listings shape a label the same way — an array of objects with a `name` — but one comes from `gh` and
// one from the REST API, and Autopilot's start gate (AC-345) has to see the same labels whichever route the
// operator's settings take.
internal static class GitHubIssueLabels
{
    // The label names on `issue`, or none when the listing did not ask for them.
    public static IReadOnlyList<string> Read(JsonElement issue) =>
        issue.TryGetProperty("labels", out var labels) ? ReadListing(labels) : [];

    // The label names in a raw label-listing response — `gh label list --json name` or the REST
    // `GET /repos/{owner}/{repo}/labels` — which shapes each entry the same way an issue's own labels array
    // does (an object with a `name`). `Read` delegates here rather than the other way round, so a
    // repo's label list (AC-519) goes through the same, and only, normalization as an issue's labels.
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
