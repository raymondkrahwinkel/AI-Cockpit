using System.Text;
using Cockpit.Plugins.Abstractions;
using Cockpit.TestSupport;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Cockpit.Plugin.YouTrack.Tests;

/// <summary>The shared upload loop (AC-116): every image in a message is attached, and one that fails does not stop the rest — the outcome counts both.</summary>
public class YouTrackAttachUploadTests : IAsyncLifetime
{
    private LoopbackHttpServer? _server;
    private string _prefix = string.Empty;
    private int _requests;

    public async Task InitializeAsync()
    {
        _server = await LoopbackHttpServer.StartAsync(_CountAndAnswerAsync);
        _prefix = _server.BaseUrl;
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

    private async Task _CountAndAnswerAsync(HttpContext context)
    {
        Interlocked.Increment(ref _requests);

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
