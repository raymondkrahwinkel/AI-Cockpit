
namespace Cockpit.Plugin.YouTrack.Tests;

// Which issues the cockpit asks YouTrack for (#48, #75). The default matters: an issue that is done is work that is
// over, and offering it in a picker is offering to start something that finished. And the operator's own filter has
// to *replace* that default, not be bolted in front of it — someone who writes "State: Done" means it, and a
// query that quietly kept "#Unresolved" in front would return nothing and look like a broken search.
public class YouTrackQueryTests
{
    [Fact]
    public void ByDefault_OnlyUnresolvedIssues() =>
        Assert.Equal("#Unresolved", YouTrackClient.BuildQuery(projectTags: null, filter: null, assignedToMe: false));

    [Fact]
    public void AssignedToMe_UsesYouTracksOwnClause() =>
        Assert.Equal("project:EVE #Unresolved for: me", YouTrackClient.BuildQuery(["EVE"], filter: null, assignedToMe: true));

    // AC-884: a project linked to several YouTrack prefixes queries all of them at once, YouTrack's own OR-syntax.
    [Fact]
    public void SeveralProjects_UsesYouTracksOwnOrSyntax() =>
        Assert.Equal("project: EWB, AT, EJ #Unresolved", YouTrackClient.BuildQuery(["EWB", "AT", "EJ"], filter: null, assignedToMe: false));
}
