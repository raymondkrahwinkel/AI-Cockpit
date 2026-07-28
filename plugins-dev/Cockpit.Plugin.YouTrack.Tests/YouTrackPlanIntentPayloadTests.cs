namespace Cockpit.Plugin.YouTrack.Tests;

/// <summary>
/// What "Plan in Autopilot" hands over. The stage field is the one Autopilot's start gate refuses on (AC-345), so
/// dropping it here would silently turn the gate into "refuse everything" — which is why it is asserted rather than
/// left to the dialog. Asserted with xunit's own Assert rather than the FluentAssertions the older files in this
/// project use: that package is commercially licensed from v8 on.
/// </summary>
public class YouTrackPlanIntentPayloadTests
{
    private static YouTrackIssue Issue(string? state) =>
        new("3-1", "AC-345", "A summary", "The description", "AC", state);

    [Fact]
    public void PlanIntentPayload_SendsTheStageTheIssueIsOn()
    {
        var payload = YouTrackDialogControl.PlanIntentPayload(Issue("Ready"), "https://youtrack/issue/AC-345");

        Assert.Equal("Ready", payload["stage"]);
    }

    [Fact]
    public void PlanIntentPayload_WithoutAStage_SendsAnEmptyStageRatherThanOmittingIt()
    {
        // A project whose status field this plugin cannot read yields null; the gate has to see that and refuse, so the
        // key is present and empty rather than absent.
        var payload = YouTrackDialogControl.PlanIntentPayload(Issue(null), "https://youtrack/issue/AC-345");

        Assert.True(payload.ContainsKey("stage"));
        Assert.Equal(string.Empty, payload["stage"]);
    }

    [Fact]
    public void PlanIntentPayload_CarriesWhatTheRunNeedsBesidesTheStage()
    {
        var payload = YouTrackDialogControl.PlanIntentPayload(Issue("Ready"), "https://youtrack/issue/AC-345");

        Assert.Equal("youtrack", payload["tracker"]);
        Assert.Equal("AC-345", payload["issue"]);
        Assert.Equal("A summary", payload["title"]);
        Assert.Equal("AC", payload["project"]);
        Assert.Equal("https://youtrack/issue/AC-345", payload["url"]);
    }
}
