using System.Net;
using System.Net.Sockets;
using System.Text;
using Cockpit.Plugins.Abstractions;
using FluentAssertions;

namespace Cockpit.Plugin.YouTrack.Tests;

/// <summary>The shared upload loop (AC-116): every image in a message is attached, and one that fails does not stop the rest — the outcome counts both.</summary>
public class YouTrackAttachUploadTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _prefix;
    private int _requests;

    public YouTrackAttachUploadTests()
    {
        _prefix = $"http://127.0.0.1:{_FreePort()}/";
        _listener.Prefixes.Add(_prefix);
        _listener.Start();
        _ = _ServeAsync();
    }

    [Fact]
    public async Task UploadAsync_AttachesEveryImage()
    {
        var instance = new YouTrackInstance("Personal", $"{_prefix}api", "token", "AC");
        var images = new List<SessionImageAttachment>
        {
            new("image/png", Convert.ToBase64String(Encoding.UTF8.GetBytes("one")), "pasted-image-1.png"),
            new("image/png", Convert.ToBase64String(Encoding.UTF8.GetBytes("two")), "pasted-image-2.png"),
        };

        var outcome = await YouTrackAttach.UploadAsync(new YouTrackClient(), instance, "AC-9", images, CancellationToken.None);

        outcome.Attached.Should().Be(2);
        outcome.Errors.Should().BeEmpty();
        _requests.Should().Be(2);
    }

    [Fact]
    public async Task UploadAsync_ReportsAFailedImageButAttachesTheRest()
    {
        var instance = new YouTrackInstance("Personal", $"{_prefix}api", "token", "AC");
        var images = new List<SessionImageAttachment>
        {
            new("image/png", "!!!not-base64!!!", "bad.png"),
            new("image/png", Convert.ToBase64String(Encoding.UTF8.GetBytes("good")), "good.png"),
        };

        var outcome = await YouTrackAttach.UploadAsync(new YouTrackClient(), instance, "AC-9", images, CancellationToken.None);

        outcome.Attached.Should().Be(1);
        outcome.Errors.Should().ContainSingle();
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

            Interlocked.Increment(ref _requests);
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
