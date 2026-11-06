using System.Net;
using System.Net.Http.Headers;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

public class DepotApiTests
{
    private const string Token = "secret-access-token";

    // Layer 2: auth status mapping and credential redaction for the same API operation.
    [Fact]
    public async Task GetAsync_MapsSpecialStatusesWithoutExposingAuthorization()
    {
        Assert.Equal("Meetings/assets/a b.png", Files.DepotNoteImages.Resolve("Meetings/note.md", "assets/a%20b.png?ignored#fragment"));
        Assert.Equal("shared/a.png", Files.DepotNoteImages.Resolve("Meetings/note.md", "../shared/./a.png"));
        foreach (var target in new[] { "../../private.png", "/private.png", "\\\\host\\private.png", "https://host/private.png", "%2e%2e/private.png", "%252e%252e/private.png", "%2fprivate.png", "assets/%00.png" })
        {
            Assert.Throws<Cockpit.Plugins.Abstractions.Files.FileSourceException>(() => Files.DepotNoteImages.Resolve("Meetings/note.md", target));
        }
        await _AssertStatusAsync(HttpStatusCode.Unauthorized, DepotApiOutcome.NotSignedIn);
        await _AssertStatusAsync(HttpStatusCode.Forbidden, DepotApiOutcome.Forbidden);
        await _AssertImageIsolationAsync();
    }

    private static async Task _AssertImageIsolationAsync()
    {
        var host = Substitute.For<ICockpitHost>();
        var connection = new DepotConnectionRegistration("c1", "Work", "https://depot.example.com");
        var replies = new Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>>();
        host.CreateAuthorizedHandler(connection.McpServerName).Returns(_ => new _ImageHandler(replies));
        var images = new Files.DepotNoteImages(host, () => [connection]);
        var file = Contracts.DepotFileRefs.Create(connection.Id, "org/project", "Meetings/note.md");
        replies.Enqueue(request =>
        {
            Assert.Equal("/sync/org/project/artifact", request.RequestUri?.AbsolutePath);
            Assert.Equal("?path=Meetings%2Fassets%2Fimage.png", request.RequestUri?.Query);
            Assert.Empty(request.Headers.IfNoneMatch);
            return Task.FromResult(_ImageResponse([1, 2, 3]));
        });
        Assert.Equal(new byte[] { 1, 2, 3 }, await images.ReadAsync(file, "assets/image.png", CancellationToken.None));
        replies.Enqueue(request =>
        {
            Assert.Equal("W/\"version\"", Assert.Single(request.Headers.IfNoneMatch).ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
        });
        Assert.Equal(new byte[] { 1, 2, 3 }, await images.ReadAsync(file, "assets/image.png", CancellationToken.None));

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var obsolete = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        replies.Enqueue(_ =>
        {
            started.SetResult();
            return obsolete.Task;
        });
        var old = images.ReadAsync(file, "assets/image.png", CancellationToken.None);
        await started.Task;
        images.Clear();
        obsolete.SetResult(_ImageResponse([4, 5, 6]));
        var failure = await Record.ExceptionAsync(() => old);
        Assert.True(failure is OperationCanceledException or Cockpit.Plugins.Abstractions.Files.FileSourceException);
        replies.Enqueue(request =>
        {
            Assert.Empty(request.Headers.IfNoneMatch);
            return Task.FromResult(_ImageResponse([7, 8, 9]));
        });
        Assert.Equal(new byte[] { 7, 8, 9 }, await images.ReadAsync(file, "assets/image.png", CancellationToken.None));
        replies.Enqueue(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        await Assert.ThrowsAsync<Cockpit.Plugins.Abstractions.Files.FileSourceException>(() => images.ReadAsync(file, "assets/image.png", CancellationToken.None));
        replies.Enqueue(request =>
        {
            Assert.Empty(request.Headers.IfNoneMatch);
            return Task.FromResult(_ImageResponse(new byte[Cockpit.Plugins.Abstractions.Files.FileImageChannel.MaxBytes + 1]));
        });
        await Assert.ThrowsAsync<Cockpit.Plugins.Abstractions.Files.FileSourceException>(() => images.ReadAsync(file, "assets/image.png", CancellationToken.None));
        Assert.Empty(replies);
        images.Clear();
    }

    private static HttpResponseMessage _ImageResponse(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        response.Headers.ETag = new EntityTagHeaderValue("\"version\"", isWeak: true);
        return response;
    }

    private sealed class _ImageHandler(Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> replies) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            replies.Dequeue()(request);
    }

    private static async Task _AssertStatusAsync(HttpStatusCode statusCode, DepotApiOutcome expected)
    {
        var host = Substitute.For<ICockpitHost>();
        var connection = new DepotConnectionRegistration("c1", "Work", "https://depot.example.com");
        host.CreateAuthorizedHandler(connection.McpServerName).Returns(new _AuthorizedHandler(statusCode));
        using var api = new DepotApi(host, connection);

        var result = await api.GetAsync("cockpit/projects", CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
        Assert.DoesNotContain("Authorization", result.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Token, result.ToString(), StringComparison.Ordinal);
    }

    private sealed class _AuthorizedHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent($"Authorization: Bearer {Token}"),
                RequestMessage = request,
            });
        }
    }
}
