using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;

namespace Cockpit.Plugin.YouTrack.Tests;

/// <summary>The attachment upload (AC-14): a multipart POST to the issue's attachments endpoint, authenticated with the instance's bearer token, carrying the file bytes.</summary>
public class YouTrackClientAttachTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _prefix;

    private string? _path;
    private string? _method;
    private string? _authorization;
    private string? _contentType;
    private string _body = string.Empty;

    public YouTrackClientAttachTests()
    {
        _prefix = $"http://127.0.0.1:{_FreePort()}/";
        _listener.Prefixes.Add(_prefix);
        _listener.Start();
        _ = _ServeAsync();
    }

    [Fact]
    public async Task AttachFileAsync_PostsMultipartWithBearerTokenToTheIssueEndpoint()
    {
        var client = new YouTrackClient();
        var bytes = Encoding.UTF8.GetBytes("the-image-bytes");

        await client.AttachFileAsync($"{_prefix}api", "perm-token", "AC-14", "pasted-image-1.png", bytes, "image/png", CancellationToken.None);

        _method.Should().Be("POST");
        _path.Should().Be("/api/issues/AC-14/attachments");
        _authorization.Should().Be("Bearer perm-token");
        _contentType.Should().StartWith("multipart/form-data");
        _body.Should().Contain("pasted-image-1.png").And.Contain("the-image-bytes");
    }

    [Fact]
    public async Task AttachFileAsync_ThrowsWithYouTrackReasonOnRefusal()
    {
        var client = new YouTrackClient();

        // Point at a port with nothing listening → the send fails; the client surfaces it rather than swallowing.
        var act = () => client.AttachFileAsync($"http://127.0.0.1:{_FreePort()}/api", "t", "AC-1", "x.png", [1, 2, 3], "image/png", CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
    }

    private async Task _ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            _method = context.Request.HttpMethod;
            _path = context.Request.Url!.AbsolutePath;
            _authorization = context.Request.Headers["Authorization"];
            _contentType = context.Request.ContentType;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            {
                _body = await reader.ReadToEndAsync();
            }

            context.Response.StatusCode = 200;
            var ok = Encoding.UTF8.GetBytes("""{ "id": "1" }""");
            await context.Response.OutputStream.WriteAsync(ok);
            context.Response.Close();
        }
    }

    private static int _FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return port;
    }

    public void Dispose()
    {
        if (_listener.IsListening)
        {
            _listener.Stop();
        }

        _listener.Close();
    }
}
