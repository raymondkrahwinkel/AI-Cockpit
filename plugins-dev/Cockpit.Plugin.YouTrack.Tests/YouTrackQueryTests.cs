using FluentAssertions;

namespace Cockpit.Plugin.YouTrack.Tests;

/// <summary>
/// Which issues the cockpit asks YouTrack for (#48, #75). The default matters: an issue that is done is work that is
/// over, and offering it in a picker is offering to start something that finished. And the operator's own filter has
/// to <em>replace</em> that default, not be bolted in front of it — someone who writes "State: Done" means it, and a
/// query that quietly kept "#Unresolved" in front would return nothing and look like a broken search.
/// </summary>
public class YouTrackQueryTests
{
    [Fact]
    public void ByDefault_OnlyUnresolvedIssues() =>
        YouTrackClient.BuildQuery(projectTag: null, filter: null, assignedToMe: false)
            .Should().Be("#Unresolved");

    [Fact]
    public void AProject_NarrowsIt() =>
        YouTrackClient.BuildQuery("EVE", filter: null, assignedToMe: false)
            .Should().Be("project:EVE #Unresolved");

    [Fact]
    public void AssignedToMe_UsesYouTracksOwnClause() =>
        YouTrackClient.BuildQuery("EVE", filter: null, assignedToMe: true)
            .Should().Be("project:EVE #Unresolved for: me");

    [Fact]
    public void TheOperatorsFilter_ReplacesTheDefault_RatherThanBeingAddedToIt() =>
        YouTrackClient.BuildQuery("EVE", "State: {In Progress}", assignedToMe: true)
            .Should().Be("project:EVE State: {In Progress} for: me");

    [Fact]
    public void AFilterThatIsOnlySpaces_IsNoFilter() =>
        YouTrackClient.BuildQuery(null, "   ", assignedToMe: false).Should().Be("#Unresolved");
}
