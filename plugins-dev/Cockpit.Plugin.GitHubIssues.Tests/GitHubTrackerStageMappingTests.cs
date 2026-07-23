using Cockpit.Plugins.Abstractions.Tracking;
using FluentAssertions;

namespace Cockpit.Plugin.GitHubIssues.Tests;

/// <summary>
/// AC-202: a GitHub issue has no status field, so its stage is a label. The provider maps Autopilot's tracker-neutral
/// lifecycle stages to conventional label names, which SetStageAsync adds as the run reaches each stage.
/// </summary>
public class GitHubTrackerStageMappingTests
{
    [Theory]
    [InlineData(TrackerWorkStage.InProgress, "in progress")]
    [InlineData(TrackerWorkStage.InReview, "in review")]
    [InlineData(TrackerWorkStage.Done, "done")]
    public void SuggestStageName_MapsEachStage_ToAConventionalLabel(TrackerWorkStage stage, string expected) =>
        new GitHubTrackerProvider().SuggestStageName(stage).Should().Be(expected);
}
