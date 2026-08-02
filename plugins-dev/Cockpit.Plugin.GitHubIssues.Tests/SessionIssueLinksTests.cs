
namespace Cockpit.Plugin.GitHubIssues.Tests;

// Linking an issue to a session is also what makes that session recognisable (#AC-310). Before this, only the
// route that started a *new* session labelled anything — an issue tied to a session already running left
// its sidebar row reading "default - 3", which is the one case where you most want to tell four panes apart.
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

        Assert.Equal("hello-world#42 — Link to session leaves the name alone", host.Statuslines["pane-1"]);
    }

    [Fact]
    public void Link_SuggestsTheIssueAsTheName_RatherThanTakingIt()
    {
        var host = new FakeCockpitHost();
        var links = new SessionIssueLinks(host);

        // The fake throws on SetSessionName: a plugin that renamed outright would erase a name the operator chose,
        // and the host is the only party that can tell the two apart.
        links.Link("pane-1", Issue);

        Assert.Equal("hello-world#42", host.SuggestedNames["pane-1"]);
    }

    [Fact]
    public void Link_WithNoPane_LabelsNothing()
    {
        var host = new FakeCockpitHost();
        var links = new SessionIssueLinks(host);

        links.Link(string.Empty, Issue);

        Assert.Empty(host.Statuslines);
        Assert.Empty(host.SuggestedNames);
    }

    [Fact]
    public void Unlink_LeavesTheStatuslineAlone()
    {
        var host = new FakeCockpitHost();
        var links = new SessionIssueLinks(host);
        links.Link("pane-1", Issue);

        links.Unlink("pane-1");

        // The statusline is shared with the agent and with flows, and there is no way to read it back to tell whose
        // text is on it now. Unlinking therefore writes nothing rather than risking wiping live progress.
        Assert.Equal("hello-world#42 — Link to session leaves the name alone", host.Statuslines["pane-1"]);
    }
}
