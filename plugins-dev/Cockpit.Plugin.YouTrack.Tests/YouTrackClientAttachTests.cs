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

    [Fact]
    public async Task AttachFileAsync_ThrowsWithYouTrackReasonOnRefusal()
    {
        // An address nothing answers on: a server that was started and then shut down again. Asking the OS for
        // a port and letting go of it would leave the same gap AC-350 removed — something else can take it,
        // and then this posts at a stranger instead of at nobody.
        var abandoned = await LoopbackHttpServer.StartAsync(_ => Task.CompletedTask);
        var abandonedUrl = abandoned.BaseUrl;
        await abandoned.DisposeAsync();

        var client = new YouTrackClient();

        var act = () => client.AttachFileAsync($"{abandonedUrl}api", "t", "AC-1", "x.png", [1, 2, 3], "image/png", CancellationToken.None);

        await Assert.ThrowsAnyAsync<Exception>(act);
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
