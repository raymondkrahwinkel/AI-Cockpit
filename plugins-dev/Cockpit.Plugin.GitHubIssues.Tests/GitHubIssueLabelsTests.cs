using System.Text.Json;

namespace Cockpit.Plugin.GitHubIssues.Tests;

// Reading an issue's labels out of either listing's payload — what Autopilot's start gate keys on (AC-345), since
// GitHub has no stage of its own. Asserted with xunit's own Assert rather than the FluentAssertions the older files
// in this project use: that package is commercially licensed from v8 on.
public class GitHubIssueLabelsTests
{
    [Fact]
    public void Read_TakesEveryLabelName()
    {
        var issue = _Parse("""{ "number": 1, "labels": [{ "name": "ready" }, { "name": "bug" }] }""");

        Assert.Equal(new[] { "ready", "bug" }, GitHubIssueLabels.Read(issue));
    }

    [Fact]
    public void Read_KeepsALabelThatContainsAComma()
    {
        // Why the intent carries one label per line: a comma is legal in a GitHub label, a newline is not.
        Assert.Equal(new[] { "ready, honestly" }, GitHubIssueLabels.Read(_Parse("""{ "labels": [{ "name": "ready, honestly" }] }""")));
    }

    [Fact]
    public void ReadListing_TakesEveryLabelName_FromARawLabelListing()
    {
        // The shape gh label list --json name / GET /repos/{owner}/{repo}/labels return: an array of {name}
        // objects, not wrapped in a "labels" property the way an issue carries its own (AC-519).
        var labels = _Parse("""[{ "name": "bug" }, { "name": "in progress" }]""");

        Assert.Equal(["bug", "in progress"], GitHubIssueLabels.ReadListing(labels));
    }

    private static JsonElement _Parse(string json) => JsonDocument.Parse(json).RootElement;
}
