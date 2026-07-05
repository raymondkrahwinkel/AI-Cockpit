using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Notifications;
using Cockpit.Infrastructure.Notifications;
using FluentAssertions;

namespace Cockpit.Core.Tests.Notifications;

/// <summary>
/// Verifies the real webhook POST against a local <see cref="HttpListener"/> sink — never a real
/// external Discord URL. Asserts the request is a POST of <c>application/json</c> whose body carries
/// the notification content in the <c>{"content":...}</c> shape.
/// </summary>
public class DiscordWebhookNotifierTests
{
    [Fact]
    public async Task NotifyAsync_PostsJsonWithContent_ToTheWebhookUrl()
    {
        using var sink = new HttpListenerSink();
        sink.Start();

        var notifier = new DiscordWebhookNotifier(new HttpClient(), NullLogger<DiscordWebhookNotifier>.Instance);
        await notifier.NotifyAsync(sink.Url, new AttentionNotification("Claude 3", "Needs attention"));

        var request = await sink.WaitForRequestAsync();

        request.Method.Should().Be("POST");
        request.ContentType.Should().StartWith("application/json");
        request.Body.Should().Contain("\"content\"");
        request.Body.Should().Contain("Claude 3");
        request.Body.Should().Contain("Needs attention");
    }

    private sealed class HttpListenerSink : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly TaskCompletionSource<CapturedRequest> _received =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HttpListenerSink()
        {
            // Port 0 lets the OS pick a free loopback port, avoiding collisions across parallel tests.
            var port = GetFreePort();
            Url = $"http://127.0.0.1:{port}/webhook/";
            _listener.Prefixes.Add(Url);
        }

        public string Url { get; }

        public void Start()
        {
            _listener.Start();
            _ = AcceptOneAsync();
        }

        public Task<CapturedRequest> WaitForRequestAsync() =>
            _received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        private async Task AcceptOneAsync()
        {
            try
            {
                var context = await _listener.GetContextAsync();
                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                var body = await reader.ReadToEndAsync();

                _received.TrySetResult(new CapturedRequest(
                    context.Request.HttpMethod,
                    context.Request.ContentType,
                    body));

                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                context.Response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                // Listener stopped before a request arrived — the test's WaitAsync timeout reports it.
            }
        }

        private static int GetFreePort()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
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

            ((IDisposable)_listener).Dispose();
        }
    }

    private sealed record CapturedRequest(string Method, string? ContentType, string Body);
}
