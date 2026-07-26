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
    public void Unlink_LeavesTheStatuslineAlone()
    {
        var host = new FakeCockpitHost();
        var links = new SessionIssueLinks(host);
        links.Link("pane-1", LinkTo(Issue));

        links.Unlink("pane-1");

        // The statusline is shared with the agent and with flows, and there is no way to read it back to tell whose
        // text is on it now. Unlinking therefore writes nothing rather than risking wiping live progress.
        host.Statuslines["pane-1"].Should().Be("AC-310 — Link to session leaves the name alone");
    }

    private static LinkedIssue LinkTo(YouTrackIssue issue) =>
        new(new YouTrackInstance("Personal", "https://youtrack.example/api", "token", "AC"), issue);
}
