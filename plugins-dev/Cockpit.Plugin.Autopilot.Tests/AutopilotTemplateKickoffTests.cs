namespace Cockpit.Plugin.Autopilot.Tests;

// The plan-flow kickoff (AC-189, slice 3): the operator's template choice becomes the CEO's opening turn. A chosen
// template's body is resolved (its {{issue.*}} tokens filled from the triggering item) and used as the kickoff instead
// of the hardcoded source kickoff; no template keeps the current behaviour exactly — the source kickoff for a
// tracker-triggered run, or no kickoff for a CEO-first run.
public class AutopilotTemplateKickoffTests
{
    private static readonly AutopilotPlanSource _Source =
        new("youtrack", "AC-138", "Reading levels", "Add reading levels to the chat view.", "https://youtrack.example/issue/AC-138");

    [Fact]
    public void NoTemplate_WithSource_KeepsTheSourceKickoff()
    {
        var kickoff = AutopilotTemplateKickoff.Build(template: null, _Source);

        Assert.Equal(AutopilotCeoBrief.SourceKickoff(_Source), kickoff.Message);
        Assert.Empty(kickoff.MissingPlaceholders);
    }

    [Fact]
    public void NoTemplate_NoSource_LeavesTheCeoIdle()
    {
        var kickoff = AutopilotTemplateKickoff.Build(template: null, source: null);

        Assert.Null(kickoff.Message);
        Assert.Empty(kickoff.MissingPlaceholders);
    }

    [Fact]
    public void ChosenTemplate_ResolvesItsBodyFromTheSource_AsTheKickoff()
    {
        var template = AutopilotTemplate.ForPlugin("youtrack", new(
            "youtrack.bugfix",
            "Bug fix",
            "Fix {{issue.id}}: \"{{issue.title}}\" on {{issue.tracker}}. {{issue.description}}"));

        var kickoff = AutopilotTemplateKickoff.Build(template, _Source);

        Assert.Equal("Fix AC-138: \"Reading levels\" on youtrack. Add reading levels to the chat view.", kickoff.Message);
        Assert.Empty(kickoff.MissingPlaceholders);
    }

    [Fact]
    public void ChosenTemplate_FillsIssueUrlFromTheSource_AndDoesNotReportItMissing()
    {
        // The source now carries the item's url (AC-189), so {{issue.url}} resolves to the real link and is no longer
        // reported missing — the gebrek where it always resolved empty. Only {{input.branch}} (no operator input) is left blank.
        var template = AutopilotTemplate.ForPlugin("youtrack", new(
            "t", "T", "Fix {{issue.id}} at {{issue.url}} on branch {{input.branch}}."));

        var kickoff = AutopilotTemplateKickoff.Build(template, _Source);

        Assert.Equal("Fix AC-138 at https://youtrack.example/issue/AC-138 on branch .", kickoff.Message);
        Assert.Equal(new[] { "input.branch" }, kickoff.MissingPlaceholders);
        Assert.DoesNotContain("issue.url", kickoff.MissingPlaceholders);
    }

    [Fact]
    public void SourceData_CarriesTheUrl_KeyedTheWayTheResolverExpects()
    {
        var data = AutopilotTemplateKickoff.SourceData(_Source);

        Assert.NotNull(data);
        Assert.Equal("https://youtrack.example/issue/AC-138", data["url"]);
    }

    [Fact]
    public void ChosenTemplate_ThatResolvesToOnlyBlankTokens_LeavesTheCeoIdleRatherThanSendingBlank()
    {
        // A CEO-first run (no source) with an issue-only template resolves to whitespace; that must not submit an empty
        // turn — leave the CEO idle so it asks the operator what the run should achieve.
        var template = AutopilotTemplate.ForUser("u", "U", "{{issue.title}}");

        var kickoff = AutopilotTemplateKickoff.Build(template, source: null);

        Assert.Null(kickoff.Message);
        Assert.Equal("issue.title", Assert.Single(kickoff.MissingPlaceholders));
    }
}
