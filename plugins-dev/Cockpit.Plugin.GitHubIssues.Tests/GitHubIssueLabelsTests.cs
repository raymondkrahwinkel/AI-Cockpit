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
    public void Read_WithoutLabelsInThePayload_ReadsAsNoneRatherThanThrowing()
    {
        // A listing that did not ask for labels, and one whose issue simply has none, both come back empty — the gate
        // treats that as "cannot tell" and refuses, which is the safe half of the two.
        Assert.Empty(GitHubIssueLabels.Read(_Parse("""{ "number": 1 }""")));
        Assert.Empty(GitHubIssueLabels.Read(_Parse("""{ "number": 1, "labels": [] }""")));
        Assert.Empty(GitHubIssueLabels.Read(_Parse("""{ "number": 1, "labels": null }""")));
    }

    [Fact]
    public void Read_SkipsALabelWithoutAUsableName()
    {
        var issue = _Parse("""{ "labels": [{ "colour": "f00" }, { "name": "" }, { "name": "ready" }] }""");

        Assert.Equal(new[] { "ready" }, GitHubIssueLabels.Read(issue));
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

    [Fact]
    public void ReadListing_IgnoresFieldsBeyondName_TheRestApisLabelsAlwaysCarry()
    {
        var labels = _Parse("""[{ "id": 1, "name": "bug", "color": "d73a4a", "description": null }]""");

        Assert.Equal(["bug"], GitHubIssueLabels.ReadListing(labels));
    }

    [Fact]
    public void ReadListing_ANonArrayResponse_ReadsAsNoneRatherThanThrowing()
    {
        // An error body ({"message": "Not Found"}) reaching here (it should not, given EnsureSuccessStatusCode
        // upstream) must not crash the label dropdown — it degrades to "no labels offered".
        Assert.Empty(GitHubIssueLabels.ReadListing(_Parse("""{ "message": "Not Found" }""")));
    }

    [Fact]
    public void Read_DelegatesToReadListing_SoThereIsOnlyOneNormalization()
    {
        // AC-519 (AC5): a repo's label list and an issue's own labels must read the same broken/odd shapes the same
        // way — proven here by feeding Read the exact array ReadListing already handles for a listing.
        var issue = _Parse("""{ "labels": [{ "colour": "f00" }, { "name": "" }, { "name": "ready" }] }""");
        var listing = _Parse("""[{ "colour": "f00" }, { "name": "" }, { "name": "ready" }]""");

        Assert.Equal(GitHubIssueLabels.ReadListing(listing), GitHubIssueLabels.Read(issue));
    }

    private static JsonElement _Parse(string json) => JsonDocument.Parse(json).RootElement;
}
