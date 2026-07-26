using FluentAssertions;

namespace Cockpit.Plugin.GitHubIssues.Tests;

/// <summary>
/// Linking an issue to a session is also what makes that session recognisable (#AC-310). Before this, only the
/// route that started a <em>new</em> session labelled anything — an issue tied to a session already running left
/// its sidebar row reading "default - 3", which is the one case where you most want to tell four panes apart.
/// </summary>
public class SessionIssueLinksTests
{
    private static readonly GitHubIssue Issue =
        new(42, "Link to session leaves the name alone", "https://github.com/octocat/hello-world/issues/42", null, "octocat/hello-world");

    [Fact]
    public void Link_SetsTheStatuslineOfThePaneItLinksTo()
    {
        var host = new FakeCockpitHost();
        var links = new SessionIssueLinks(host);

        links.Link("pane-1", Issue);

        host.Statuslines["pane-1"].Should().Be("hello-world#42 — Link to session leaves the name alone");
    }

    [Fact]
    public void Link_SuggestsTheIssueAsTheName_RatherThanTakingIt()
    {
        var host = new FakeCockpitHost();
        var links = new SessionIssueLinks(host);

        // The fake throws on SetSessionName: a plugin that renamed outright would erase a name the operator chose,
        // and the host is the only party that can tell the two apart.
        links.Link("pane-1", Issue);

        host.SuggestedNames["pane-1"].Should().Be("hello-world#42");
    }

    [Fact]
    public void Link_WithNoPane_LabelsNothing()
    {
        var host = new FakeCockpitHost();
        var links = new SessionIssueLinks(host);

        links.Link(string.Empty, Issue);

        host.Statuslines.Should().BeEmpty();
        host.SuggestedNames.Should().BeEmpty();
    }

    [Fact]
    public void Unlink_ClearsTheStatuslineTheLinkPutThere()
    {
        var host = new FakeCockpitHost();
        var links = new SessionIssueLinks(host);
        links.Link("pane-1", Issue);

        links.Unlink("pane-1");

        // Cleared, not left saying hello-world#42 — a session that says it is on an issue you put down is worse
        // than one that says nothing. The name stays: what it was before the link is not this plugin's to restore.
        host.Statuslines["pane-1"].Should().BeEmpty();
        host.SuggestedNames["pane-1"].Should().Be("hello-world#42");
    }

    [Fact]
    public void Unlink_OfAPaneThatWasNeverLinked_TouchesNothing()
    {
        var host = new FakeCockpitHost();
        var links = new SessionIssueLinks(host);

        links.Unlink("pane-1");

        host.Statuslines.Should().BeEmpty();
    }
}
