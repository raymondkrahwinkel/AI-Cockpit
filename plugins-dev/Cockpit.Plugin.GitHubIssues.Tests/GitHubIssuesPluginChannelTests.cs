using System.Text.Json;
using Cockpit.Plugin.GitHubIssues.Contracts;

namespace Cockpit.Plugin.GitHubIssues.Tests;

// AC-1396: before this split, the dialog and the session header held SessionIssueLinks themselves; now the backend
// part keeps it and the UI part links over the channel. This pins that round trip: the link lands where the header
// reads it back, the session is labelled, and the change is published for the headers to follow.
public class GitHubIssuesPluginChannelTests
{
    private static readonly GitHubIssue Issue =
        new(42, "Link to session leaves the name alone", "https://github.com/octocat/hello-world/issues/42", null, "octocat/hello-world");

    [Fact]
    public async Task Link_OverTheChannel_LinksThePane_AndPublishesTheChange()
    {
        var channel = new InProcessChannel();
        var host = new FakeCockpitHost { Channel = channel };
        using var plugin = new GitHubIssuesPlugin();
        plugin.Initialize(host);

        await channel.InvokeAsync(GitHubIssuesChannel.Link, _Payload(new GitHubIssuesLinkRequest("pane-1", Issue, "/home/operator/repo")));
        var answer = await channel.InvokeAsync(GitHubIssuesChannel.LinkedIssue, _Payload(new GitHubIssuesPaneRequest("pane-1")));

        var linked = answer.Deserialize<GitHubIssue>(GitHubIssuesChannel.Json);
        var published = Assert.Single(channel.Published);
        var changed = published.Payload.Deserialize<GitHubIssuesLinkChanged>(GitHubIssuesChannel.Json);
        Assert.Equal(Issue.Number, linked?.Number);
        Assert.Equal("hello-world#42 — Link to session leaves the name alone", host.Statuslines["pane-1"]);
        Assert.Equal(GitHubIssuesChannel.LinkChanged, published.Name);
        Assert.Equal("pane-1", changed?.PaneId);
        Assert.Equal(Issue.Number, changed?.Issue?.Number);
    }

    private static JsonElement _Payload(object request) =>
        JsonSerializer.SerializeToElement(request, request.GetType(), GitHubIssuesChannel.Json);
}
