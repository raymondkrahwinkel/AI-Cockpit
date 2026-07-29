using System.Net;
using Cockpit.Core.Notifications;
using Cockpit.TestSupport;
using Cockpit.Infrastructure.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Core.Tests.Notifications;

/// <summary>
/// Verifies the real webhook POST against a local <see cref="LoopbackHttpServer"/> sink — never a real
/// external Discord URL. Asserts the request is a POST of <c>application/json</c> whose body carries
/// the notification content in the <c>{"content":...}</c> shape.
/// </summary>
public class DiscordWebhookNotifierTests
{
    [Fact]
    public async Task NotifyAsync_PostsJsonWithContent_ToTheWebhookUrl()
    {
        var received = new TaskCompletionSource<CapturedRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var sink = await LoopbackHttpServer.StartAsync(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();

            received.TrySetResult(new CapturedRequest(
                context.Request.Method,
                context.Request.Path.ToString(),
                context.Request.ContentType,
                body));
            context.Response.StatusCode = (int)HttpStatusCode.NoContent;
        });

        var notifier = new DiscordWebhookNotifier(new HttpClient(), NullLogger<DiscordWebhookNotifier>.Instance);
        await notifier.NotifyAsync($"{sink.BaseUrl}webhook/", new AttentionNotification("Claude 3", "Needs attention"));

        var request = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("POST", request.Method);
        Assert.Equal("/webhook/", request.Path);
        Assert.StartsWith("application/json", request.ContentType);
        Assert.Contains("\"content\"", request.Body);
        Assert.Contains("Claude 3", request.Body);
        Assert.Contains("Needs attention", request.Body);
    }

    private sealed record CapturedRequest(string Method, string Path, string? ContentType, string Body);
}
