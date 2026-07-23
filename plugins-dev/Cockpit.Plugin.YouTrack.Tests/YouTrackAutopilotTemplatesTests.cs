using FluentAssertions;

namespace Cockpit.Plugin.YouTrack.Tests;

/// <summary>
/// The Autopilot templates this plugin contributes (AC-189/AC-217): bug fix, feature, and the epic template that plans a
/// YouTrack epic — an issue whose real work is its "parent for" child issues — as one coherent run to one PR. Asserted
/// on the pure registration so the epic instruction (fetch the children via the links, one plan, one PR) does not drift.
/// </summary>
public class YouTrackAutopilotTemplatesTests
{
    [Fact]
    public void All_RegistersBugFixFeatureAndEpic()
    {
        YouTrackAutopilotTemplates.All.Select(template => template.Id)
            .Should().Contain(["youtrack.bugfix", "youtrack.feature", "youtrack.epic"]);
    }

    [Fact]
    public void Epic_InstructsTheCeoToPullChildIssues_PlanAsOneRun_AndLandOnePr()
    {
        var epic = YouTrackAutopilotTemplates.All.Single(template => template.Id == "youtrack.epic");

        epic.Name.Should().Be("Epic");
        // Fetch the sub-items from the "parent for" child links (AC-158's shape), not from the description.
        epic.Body.Should().Contain("parent for");
        epic.Body.Should().Contain("child issues");
        epic.Body.Should().Contain("read tools");
        epic.Body.Should().Contain("not the description");
        // One coherent run to one PR, with the ids recorded so the PR names what it closes.
        epic.Body.Should().Contain("ONE plan");
        epic.Body.Should().Contain("ONE pull request");
        epic.Body.Should().Contain("{{issue.id}}");
        // Carries the issue placeholders Autopilot fills at run time.
        epic.RequiredPlaceholders.Should().Contain(["issue.id", "issue.title"]);
    }
}
