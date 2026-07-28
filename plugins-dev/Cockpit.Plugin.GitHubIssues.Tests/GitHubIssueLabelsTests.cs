using System.Text.Json;

namespace Cockpit.Plugin.GitHubIssues.Tests;

/// <summary>
/// Reading an issue's labels out of either listing's payload — what Autopilot's start gate keys on (AC-345), since
/// GitHub has no stage of its own. Asserted with xunit's own Assert rather than the FluentAssertions the older files
/// in this project use: that package is commercially licensed from v8 on.
/// </summary>
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

    private static JsonElement _Parse(string json) => JsonDocument.Parse(json).RootElement;
}
