using System.Text;
using Cockpit.TestSupport;
using Microsoft.AspNetCore.Http;

namespace Cockpit.Plugin.YouTrack.Tests;

// The attachment upload (AC-14): a multipart POST to the issue's attachments endpoint, authenticated with the instance's bearer token, carrying the file bytes.
public class YouTrackClientAttachTests : IAsyncLifetime
{
    private LoopbackHttpServer? _server;
    private string _prefix = string.Empty;

    private string? _path;
    private string? _method;
    private string? _authorization;
    private string? _contentType;
    private string _body = string.Empty;

    public async Task InitializeAsync()
    {
        _server = await LoopbackHttpServer.StartAsync(_RecordAndAnswerAsync);
        _prefix = _server.BaseUrl;
    }

    [Fact]
    public async Task AttachFileAsync_PostsMultipartWithBearerTokenToTheIssueEndpoint()
    {
        var client = new YouTrackClient();
        var bytes = Encoding.UTF8.GetBytes("the-image-bytes");

        await client.AttachFileAsync($"{_prefix}api", "perm-token", "AC-14", "pasted-image-1.png", bytes, "image/png", CancellationToken.None);

        Assert.Equal("POST", _method);
        Assert.Equal("/api/issues/AC-14/attachments", _path);
        Assert.Equal("Bearer perm-token", _authorization);
        Assert.StartsWith("multipart/form-data", _contentType);
        Assert.Contains("pasted-image-1.png", _body);
        Assert.Contains("the-image-bytes", _body);
    }

    private async Task _RecordAndAnswerAsync(HttpContext context)
    {
        _method = context.Request.Method;
        _path = context.Request.Path.ToString();
        _authorization = context.Request.Headers.TryGetValue("Authorization", out var authorization) ? authorization.ToString() : null;
        _contentType = context.Request.ContentType;

        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8))
        {
            _body = await reader.ReadToEndAsync();
        }

        await context.Response.WriteAsync("""{ "id": "1" }""");
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }
}
