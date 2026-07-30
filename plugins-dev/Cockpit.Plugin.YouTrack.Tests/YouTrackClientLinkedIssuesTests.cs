using Cockpit.Plugins.Abstractions.Tracking;
using Cockpit.TestSupport;
using Microsoft.AspNetCore.Http;

namespace Cockpit.Plugin.YouTrack.Tests;

/// <summary>
/// AC-346: reading an issue's links (<c>GET /issues/{id}/links</c>) — YouTrack groups links by (type, direction)
/// rather than one entry per linked issue, so this exercises the flattening and the per-direction name resolution
/// (<c>sourceToTarget</c> for OUTWARD, <c>targetToSource</c> for INWARD) that <see cref="ITrackerProvider.GetLinkedIssuesAsync"/>
/// promises.
/// </summary>
public class YouTrackClientLinkedIssuesTests : IAsyncLifetime
{
    private LoopbackHttpServer? _server;
    private string _prefix = string.Empty;
    private string _responseBody = "[]";

    public async Task InitializeAsync()
    {
        _server = await LoopbackHttpServer.StartAsync(context =>
        {
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync(_responseBody);
        });
        _prefix = _server.BaseUrl;
    }

    [Fact]
    public async Task GetLinkedIssuesAsync_ResolvesTheOutwardName_ForAnEpicsChildren()
    {
        // An epic reading its own "parent for" children: it is the link's source, so YouTrack reports "OUTWARD" and
        // the name to use is linkType.sourceToTarget.
        _responseBody = """
        [
            {
                "direction": "OUTWARD",
                "linkType": { "name": "Subtask", "sourceToTarget": "parent for", "targetToSource": "subtask of" },
                "issues": [
                    { "idReadable": "AC-1", "summary": "First sub", "customFields": [{ "name": "State", "value": { "name": "Ready" } }] },
                    { "idReadable": "AC-2", "summary": "Second sub", "customFields": [] }
                ]
            }
        ]
        """;

        var client = new YouTrackClient();
        var links = await client.GetLinkedIssuesAsync($"{_prefix}api", "token", "AC-EPIC", CancellationToken.None);

        Assert.Equal(2, links.Count);
        Assert.Equal(new TrackerLinkedIssue("parent for", TrackerLinkDirection.Outward, "AC-1", "First sub", "Ready"), links[0]);
        Assert.Equal(new TrackerLinkedIssue("parent for", TrackerLinkDirection.Outward, "AC-2", "Second sub", null), links[1]);
    }

    [Fact]
    public async Task GetLinkedIssuesAsync_ResolvesTheInwardName_ForASubsDependency()
    {
        // A sub reading its own "depends on" link: it is the link's target from the other issue's perspective, so
        // YouTrack reports "INWARD" here and the name to use is linkType.targetToSource.
        _responseBody = """
        [
            {
                "direction": "INWARD",
                "linkType": { "name": "Depend", "sourceToTarget": "is required for", "targetToSource": "depends on" },
                "issues": [ { "idReadable": "AC-1", "summary": "The blocker", "customFields": [] } ]
            }
        ]
        """;

        var client = new YouTrackClient();
        var links = await client.GetLinkedIssuesAsync($"{_prefix}api", "token", "AC-2", CancellationToken.None);

        var link = Assert.Single(links);
        Assert.Equal("depends on", link.LinkType);
        Assert.Equal(TrackerLinkDirection.Inward, link.Direction);
        Assert.Equal("AC-1", link.IssueId);
    }

    [Fact]
    public async Task GetLinkedIssuesAsync_OnAnIssueWithNoLinks_ReturnsEmpty()
    {
        _responseBody = "[]";

        var client = new YouTrackClient();
        var links = await client.GetLinkedIssuesAsync($"{_prefix}api", "token", "AC-1", CancellationToken.None);

        Assert.Empty(links);
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }
}
