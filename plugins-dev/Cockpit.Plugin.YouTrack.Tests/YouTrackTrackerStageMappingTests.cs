using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Tracking;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Plugin.YouTrack.Tests;

/// <summary>
/// AC-202: the YouTrack provider maps Autopilot's tracker-neutral lifecycle stages to the AC board's own "Stage"
/// vocabulary (Backlog / Develop / Review / Test / Staging / Done), so a source-triggered run moves the issue itself
/// instead of leaving it on Backlog. Merge-ready maps to Review, not Done — the merge is a human's.
/// </summary>
public class YouTrackTrackerStageMappingTests
{
    [Theory]
    [InlineData(TrackerWorkStage.InProgress, "Develop")]
    [InlineData(TrackerWorkStage.InReview, "Review")]
    [InlineData(TrackerWorkStage.Done, "Done")]
    public void SuggestStageName_MapsEachStage_ToTheBoardsOwnVocabulary(TrackerWorkStage stage, string expected) =>
        _Provider().SuggestStageName(stage).Should().Be(expected);

    private static YouTrackTrackerProvider _Provider() => new(new YouTrackSettings(Substitute.For<IPluginStorage>()));
}
