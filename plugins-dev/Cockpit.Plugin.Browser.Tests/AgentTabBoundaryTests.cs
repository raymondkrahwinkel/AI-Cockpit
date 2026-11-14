using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Cockpit.Plugin.Browser.Tests;

public class AgentTabBoundaryTests
{
    // A redirect to a non-web scheme must reach the opener as invalid_argument. The reason has to be recorded before
    // Fetch.failRequest is answered: the fake browser looks at the moment it receives that command, so no timing is involved.
    [Fact]
    public async Task RedirectToDataOrFile_IsRecordedAsInvalidArgument_BeforeTheRequestIsFailed()
    {
        await using var browser = new FakeCdpBrowser();
        using var boundary = new AgentTabBoundary(_ => true, _ => { }, new AddressPolicy(() => ["127.0.0.0/8"]));
        browser.AtFailRequest = () => boundary.TakeBlocked("T1");
        await boundary.StartAsync(browser.Upstream, CancellationToken.None);

        await browser.EventAsync("Target.attachedToTarget", new { sessionId = "S1", targetInfo = new { targetId = "T1", url = "about:blank" }, waitingForDebugger = true });
        await boundary.ReadyAsync("T1", CancellationToken.None);

        foreach (var location in new[] { "data:text/html,secret", "file:///secret" })
        {
            var failed = browser.NextFailRequest();
            await browser.EventAsync("Fetch.requestPaused", new
            {
                sessionId = "S1",
                requestId = "r1",
                request = new { url = "http://127.0.0.1:1/redirect" },
                resourceType = "Document",
                frameId = "F1",
                responseStatusCode = 302,
                responseHeaders = new[] { new { name = "Location", value = location } },
            }, "S1");
            var blocked = await failed.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("invalid_argument", blocked?.Code);
        }
    }

    // Loopback CDP endpoint: /json/version, one websocket, every command answered with an empty result.
    private sealed class FakeCdpBrowser : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly TaskCompletionSource<WebSocket> _socket = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _serve;
        private TaskCompletionSource<AgentTabBoundary.Blocked?> _failRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<AgentTabBoundary.Blocked?>? AtFailRequest { get; set; }
        public string Upstream { get; }

        public FakeCdpBrowser()
        {
            var port = FreePort();
            Upstream = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Upstream + "/");
            _listener.Start();
            _serve = ServeAsync(port);
        }

        public Task<AgentTabBoundary.Blocked?> NextFailRequest() => (_failRequest = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        public async Task EventAsync(string method, object parameters, string? session = null)
        {
            var socket = await _socket.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { method, @params = parameters, sessionId = session ?? ((dynamic)parameters).sessionId });
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        private async Task ServeAsync(int port)
        {
            try
            {
                while (true)
                {
                    var context = await _listener.GetContextAsync();
                    if (!context.Request.IsWebSocketRequest)
                    {
                        var body = Encoding.UTF8.GetBytes($$"""{"webSocketDebuggerUrl":"ws://127.0.0.1:{{port}}/cdp"}""");
                        await context.Response.OutputStream.WriteAsync(body);
                        context.Response.Close();
                        continue;
                    }
                    var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
                    _socket.TrySetResult(socket);
                    _ = AnswerAsync(socket);
                }
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { }
        }

        private async Task AnswerAsync(WebSocket socket)
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var received = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (received.MessageType == WebSocketMessageType.Close) return;
                    using var command = JsonDocument.Parse(buffer.AsMemory(0, received.Count));
                    var id = command.RootElement.GetProperty("id").GetInt32();
                    if (command.RootElement.GetProperty("method").GetString() == "Fetch.failRequest")
                    {
                        _failRequest.TrySetResult(AtFailRequest?.Invoke());
                    }
                    await socket.SendAsync(Encoding.UTF8.GetBytes("{\"id\":" + id + ",\"result\":{}}"), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException) { }
        }

        private static int FreePort()
        {
            using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Close();
            await _serve;
        }
    }
}
