using Cockpit.Plugins.Abstractions.Tracking;
using Cockpit.TestSupport;
using Microsoft.AspNetCore.Http;

namespace Cockpit.Plugin.YouTrack.Tests;

// AC-346: reading an issue's links (`GET /issues/{id}/links`) — YouTrack groups links by (type, direction)
// rather than one entry per linked issue, so this exercises the flattening and the per-direction name resolution
// (`sourceToTarget` for OUTWARD, `targetToSource` for INWARD) that `ITrackerProvider.GetLinkedIssuesAsync`
// promises.
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
    public async Task GetLinkedIssuesAsync_ResolvesTheNamePerDirection()
    {
        // OUTWARD: an epic reading its own "parent for" children is the link's source, so the name to use is
        // linkType.sourceToTarget. INWARD: a "depends on" link where this issue is the target from the other
        // issue's perspective, so the name to use is linkType.targetToSource.
        _responseBody = """
        [
            {
                "direction": "OUTWARD",
                "linkType": { "name": "Subtask", "sourceToTarget": "parent for", "targetToSource": "subtask of" },
                "issues": [
                    { "idReadable": "AC-1", "summary": "First sub", "customFields": [{ "name": "State", "value": { "name": "Ready" } }] },
                    { "idReadable": "AC-2", "summary": "Second sub", "customFields": [] }
                ]
            },
            {
                "direction": "INWARD",
                "linkType": { "name": "Depend", "sourceToTarget": "is required for", "targetToSource": "depends on" },
                "issues": [ { "idReadable": "AC-3", "summary": "The blocker", "customFields": [] } ]
            }
        ]
        """;

        var client = new YouTrackClient();
        var links = await client.GetLinkedIssuesAsync($"{_prefix}api", "token", "AC-EPIC", CancellationToken.None);

        Assert.Equal(3, links.Count);
        Assert.Equal(new TrackerLinkedIssue("parent for", TrackerLinkDirection.Outward, "AC-1", "First sub", "Ready"), links[0]);
        Assert.Equal(new TrackerLinkedIssue("parent for", TrackerLinkDirection.Outward, "AC-2", "Second sub", null), links[1]);
        Assert.Equal(new TrackerLinkedIssue("depends on", TrackerLinkDirection.Inward, "AC-3", "The blocker", null), links[2]);
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }
}
