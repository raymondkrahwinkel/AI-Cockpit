using Cockpit.TestSupport;
using Microsoft.AspNetCore.Http;

namespace Cockpit.Plugin.YouTrack.Tests;

// `YouTrackClient.GetProjectStateFieldAsync` (AC-518): the state dropdown's own source, independent
// of whichever page of issues happened to load — a real HTTP round trip against the admin projects/customFields
// endpoint, same fail-open shape as `YouTrackClient.GetProjectsAsync`.
public class YouTrackClientStateFieldTests : IAsyncLifetime
{
    private LoopbackHttpServer? _server;
    private string _prefix = string.Empty;
    private string? _path;
    private string? _authorization;
    private bool _wasCalled;

    public async Task InitializeAsync()
    {
        _server = await LoopbackHttpServer.StartAsync(_RecordAndAnswerAsync);
        _prefix = _server.BaseUrl;
    }

    [Fact]
    public async Task GetProjectStateFieldAsync_ReadsTheProjectsWholeBundle_NotJustWhatOneIssueCarries()
    {
        var client = new YouTrackClient();

        var (fieldName, values) = await client.GetProjectStateFieldAsync($"{_prefix}api", "perm-token", "AC", CancellationToken.None);

        Assert.True(_wasCalled);
        Assert.Equal("/api/admin/projects/AC/customFields", _path);
        Assert.Equal("Bearer perm-token", _authorization);
        Assert.Equal("State", fieldName);
        Assert.Equal(new[] { "Open", "Ready", "Test", "Review", "Done" }, values);
    }

    [Fact]
    public async Task GetProjectStateFieldAsync_ExcludesAResolvedValue_OverARealHttpRoundTrip()
    {
        var requests = new List<string>();
        var server = await LoopbackHttpServer.StartAsync(context =>
        {
            requests.Add(context.Request.QueryString.Value ?? string.Empty);
            return context.Response.WriteAsync(
                """[{"field":{"name":"State"},"bundle":{"values":[{"name":"Open","isResolved":false},{"name":"Done","isResolved":true}]}}]""");
        });
        var client = new YouTrackClient();

        var (fieldName, values) = await client.GetProjectStateFieldAsync(server.BaseUrl + "api", "t", "AC", CancellationToken.None);

        await server.DisposeAsync();
        Assert.Equal("State", fieldName);
        Assert.Equal(["Open"], values);
        Assert.Single(requests);
    }

    [Fact]
    public async Task GetProjectStateFieldAsync_WhenAskingForIsResolvedFailsOutright_RetriesWithThePlainFieldAndKeepsEverything()
    {
        // The one failure mode this cannot fall back on from a single response: asking for isResolved on a project
        // whose status field cannot supply it might, undocumented, refuse the whole call rather than omit the key.
        var requests = new List<string>();
        var server = await LoopbackHttpServer.StartAsync(async context =>
        {
            var query = context.Request.QueryString.Value ?? string.Empty;
            requests.Add(query);
            if (query.Contains("isResolved", StringComparison.Ordinal))
            {
                context.Response.StatusCode = 400;
                return;
            }

            await context.Response.WriteAsync("""[{"field":{"name":"State"},"bundle":{"values":[{"name":"Open"},{"name":"Done"}]}}]""");
        });
        var client = new YouTrackClient();

        var (fieldName, values) = await client.GetProjectStateFieldAsync(server.BaseUrl + "api", "t", "AC", CancellationToken.None);

        await server.DisposeAsync();
        Assert.Equal("State", fieldName);
        // The retry has no isResolved data at all, so — same as the "absent" case above — nothing is filtered:
        // exactly the pre-AC-518-follow-up behaviour, never emptier than before.
        Assert.Equal(["Open", "Done"], values);
        Assert.Equal(2, requests.Count);
    }

    private Task _RecordAndAnswerAsync(HttpContext context)
    {
        _wasCalled = true;
        _path = context.Request.Path.ToString();
        _authorization = context.Request.Headers.TryGetValue("Authorization", out var authorization) ? authorization.ToString() : null;

        return context.Response.WriteAsync(
            """
            [
              {"field":{"name":"Priority"},"bundle":{"values":[{"name":"Low"}]}},
              {"field":{"name":"State"},"bundle":{"values":[{"name":"Open"},{"name":"Ready"},{"name":"Test"},{"name":"Review"},{"name":"Done"}]}}
            ]
            """);
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }
}
