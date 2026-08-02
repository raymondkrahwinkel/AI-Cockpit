namespace Cockpit.Plugin.GitHubIssues.Tests;

// What "Plan in Autopilot" hands over. The stage field is the one Autopilot's start gate refuses on (AC-345), so
// dropping it here would silently turn the gate into "refuse everything" — which is why it is asserted rather than
// left to the dialog. Asserted with xunit's own Assert rather than the FluentAssertions the older files in this
// project use: that package is commercially licensed from v8 on.
public class GitHubPlanIntentPayloadTests
{
    private static GitHubIssue Issue(params string[] labels) =>
        new(42, "A title", "https://github.com/o/r/issues/42", "The body", "o/r") { Labels = labels };

    [Fact]
    public void PlanIntentPayload_SendsTheLabelsAsTheStage_OnePerLine()
    {
        var payload = GitHubIssuesDialogControl.PlanIntentPayload(Issue("ready", "bug"), "o/r#42");

        Assert.Equal("ready\nbug", payload["stage"]);
    }

    [Fact]
    public void PlanIntentPayload_WithoutLabels_SendsAnEmptyStageRatherThanOmittingIt()
    {
        // The gate reads a missing key and an empty value the same way — refuse — but an absent key would also be what
        // an out-of-date plugin sends, and this one is not out of date.
        var payload = GitHubIssuesDialogControl.PlanIntentPayload(Issue(), "o/r#42");

        Assert.True(payload.ContainsKey("stage"));
        Assert.Equal(string.Empty, payload["stage"]);
    }

    [Fact]
    public void PlanIntentPayload_CarriesWhatTheRunNeedsBesidesTheStage()
    {
        var payload = GitHubIssuesDialogControl.PlanIntentPayload(Issue("ready"), "o/r#42");

        Assert.Equal("github-issues", payload["tracker"]);
        Assert.Equal("o/r#42", payload["issue"]);
        Assert.Equal("A title", payload["title"]);
        Assert.Equal("o/r", payload["repository"]);
    }
}
