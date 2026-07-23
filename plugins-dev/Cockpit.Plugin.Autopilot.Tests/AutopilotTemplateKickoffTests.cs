using FluentAssertions;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The plan-flow kickoff (AC-189, slice 3): the operator's template choice becomes the CEO's opening turn. A chosen
/// template's body is resolved (its {{issue.*}} tokens filled from the triggering item) and used as the kickoff instead
/// of the hardcoded source kickoff; no template keeps the current behaviour exactly — the source kickoff for a
/// tracker-triggered run, or no kickoff for a CEO-first run.
/// </summary>
public class AutopilotTemplateKickoffTests
{
    private static readonly AutopilotPlanSource _Source =
        new("youtrack", "AC-138", "Reading levels", "Add reading levels to the chat view.");

    [Fact]
    public void NoTemplate_WithSource_KeepsTheSourceKickoff()
    {
        var kickoff = AutopilotTemplateKickoff.Build(template: null, _Source);

        kickoff.Message.Should().Be(AutopilotCeoBrief.SourceKickoff(_Source));
        kickoff.MissingPlaceholders.Should().BeEmpty();
    }

    [Fact]
    public void NoTemplate_NoSource_LeavesTheCeoIdle()
    {
        var kickoff = AutopilotTemplateKickoff.Build(template: null, source: null);

        kickoff.Message.Should().BeNull();
        kickoff.MissingPlaceholders.Should().BeEmpty();
    }

    [Fact]
    public void ChosenTemplate_ResolvesItsBodyFromTheSource_AsTheKickoff()
    {
        var template = AutopilotTemplate.ForPlugin("youtrack", new(
            "youtrack.bugfix",
            "Bug fix",
            "Fix {{issue.id}}: \"{{issue.title}}\" on {{issue.tracker}}. {{issue.description}}"));

        var kickoff = AutopilotTemplateKickoff.Build(template, _Source);

        kickoff.Message.Should().Be("Fix AC-138: \"Reading levels\" on youtrack. Add reading levels to the chat view.");
        kickoff.MissingPlaceholders.Should().BeEmpty();
    }

    [Fact]
    public void ChosenTemplate_ReportsPlaceholdersItCouldNotFill_ButNeverThrows()
    {
        // The source carries no url and no operator input, so {{issue.url}} and {{input.branch}} cannot be filled — they
        // are left blank and reported, not thrown on.
        var template = AutopilotTemplate.ForPlugin("youtrack", new(
            "t", "T", "Fix {{issue.id}} at {{issue.url}} on branch {{input.branch}}."));

        var kickoff = AutopilotTemplateKickoff.Build(template, _Source);

        kickoff.Message.Should().Be("Fix AC-138 at  on branch .");
        kickoff.MissingPlaceholders.Should().BeEquivalentTo("issue.url", "input.branch");
    }

    [Fact]
    public void ChosenTemplate_ThatResolvesToOnlyBlankTokens_LeavesTheCeoIdleRatherThanSendingBlank()
    {
        // A CEO-first run (no source) with an issue-only template resolves to whitespace; that must not submit an empty
        // turn — leave the CEO idle so it asks the operator what the run should achieve.
        var template = AutopilotTemplate.ForUser("u", "U", "{{issue.title}}");

        var kickoff = AutopilotTemplateKickoff.Build(template, source: null);

        kickoff.Message.Should().BeNull();
        kickoff.MissingPlaceholders.Should().ContainSingle().Which.Should().Be("issue.title");
    }
}
