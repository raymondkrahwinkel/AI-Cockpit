using FluentAssertions;

namespace Cockpit.Plugin.YouTrack.Tests;

/// <summary>
/// Linking a ticket to a session is also what makes that session recognisable (#AC-310). Before this, only the
/// route that started a <em>new</em> session labelled anything — a ticket tied to a session already running left
/// its sidebar row reading "default - 3", which is the one case where you most want to tell four panes apart.
/// </summary>
public class SessionIssueLinksTests
{
    private static readonly YouTrackIssue Issue =
        new("2-1", "AC-310", "Link to session leaves the name alone", null, "AC", "Open");

    [Fact]
    public void Link_SetsTheStatuslineOfThePaneItLinksTo()
    {
        var host = new FakeCockpitHost();
        var links = new SessionIssueLinks(host);

        links.Link("pane-1", LinkTo(Issue));

        host.Statuslines["pane-1"].Should().Be("AC-310 — Link to session leaves the name alone");
    }

    [Fact]
    public void Link_SuggestsTheTicketAsTheName_RatherThanTakingIt()
    {
        var host = new FakeCockpitHost();
        var links = new SessionIssueLinks(host);

        // The fake throws on SetSessionName: a plugin that renamed outright would erase a name the operator chose,
        // and the host is the only party that can tell the two apart.
        links.Link("pane-1", LinkTo(Issue));

        host.SuggestedNames["pane-1"].Should().Be("AC-310");
    }

    [Fact]
    public void Link_WithNoPane_LabelsNothing()
    {
        var host = new FakeCockpitHost();
        var links = new SessionIssueLinks(host);

        links.Link(string.Empty, LinkTo(Issue));

        host.Statuslines.Should().BeEmpty();
        host.SuggestedNames.Should().BeEmpty();
    }

    [Fact]
    public void Unlink_ClearsTheStatuslineTheLinkPutThere()
    {
        var host = new FakeCockpitHost();
        var links = new SessionIssueLinks(host);
        links.Link("pane-1", LinkTo(Issue));

        links.Unlink("pane-1");

        // Cleared, not left saying AC-310 — a session that says it is on a ticket you put down is worse than one
        // that says nothing. The name stays: what it was before the link is not this plugin's to restore.
        host.Statuslines["pane-1"].Should().BeEmpty();
        host.SuggestedNames["pane-1"].Should().Be("AC-310");
    }

    [Fact]
    public void Unlink_OfAPaneThatWasNeverLinked_TouchesNothing()
    {
        var host = new FakeCockpitHost();
        var links = new SessionIssueLinks(host);

        links.Unlink("pane-1");

        host.Statuslines.Should().BeEmpty();
    }

    private static LinkedIssue LinkTo(YouTrackIssue issue) =>
        new(new YouTrackInstance("Personal", "https://youtrack.example/api", "token", "AC"), issue);
}
