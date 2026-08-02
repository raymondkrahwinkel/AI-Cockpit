using Cockpit.Plugins.Abstractions.Tracking;

namespace Cockpit.Plugin.GitHubIssues.Tests;

// AC-202: a GitHub issue has no status field, so its stage is a label. The provider maps Autopilot's tracker-neutral
// lifecycle stages to conventional label names, which SetStageAsync adds as the run reaches each stage.
public class GitHubTrackerStageMappingTests
{
    [Theory]
    [InlineData(TrackerWorkStage.InProgress, "in progress")]
    [InlineData(TrackerWorkStage.InReview, "in review")]
    [InlineData(TrackerWorkStage.Done, "done")]
    public void SuggestStageName_MapsEachStage_ToAConventionalLabel(TrackerWorkStage stage, string expected) =>
        Assert.Equal(expected, new GitHubTrackerProvider().SuggestStageName(stage));

    [Fact]
    public void ReadToolMcpServerNames_IsEmpty_BecauseGitHubIssuesReadsViaTheGhCli_NotAnMcpServer() =>
        // AC-212: the read/write split is provider-neutral. GitHub Issues has no MCP read server (it reads through the
        // gh CLI), so it advertises no read servers — a source-triggered plan simply scopes none in for it, and the CEO
        // reads via gh instead. Only a tracker with an MCP read server (YouTrack) contributes names. Read through the
        // interface: the empty list is the default ITrackerProvider member, which GitHub does not override.
        Assert.Empty(((ITrackerProvider)new GitHubTrackerProvider()).ReadToolMcpServerNames);
}
