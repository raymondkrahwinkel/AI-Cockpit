using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Cockpit.Plugin.Browser;

// AC-1713: A thin CDP pass-through between Playwright MCP and one session's Chromium. While Raymond drives it answers
// the session's `Input.*` commands with an error (one driver per surface, as TerminalMcpTools, AC-34); after Stop
// agent it stays up so the session reads why.
internal sealed class AgentCdpProxy : IDisposable
{
    internal const string TakenOverMessage = "Raymond has taken over the agent browser: input is paused until he hands it back";

    internal const string StoppedMessage = "Raymond has stopped the agent browser";

    private static readonly HttpClient Http = new();

    private readonly HttpListener _listener = new();
    private readonly string _upstream;
    private readonly int _upstreamPort;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<WebSocket, byte> _sockets = new();
    private volatile bool _operatorDrives;
    private volatile bool _stopped;

    private AgentCdpProxy(string upstream, int upstreamPort, int port)
    {
        _upstream = upstream;
        _upstreamPort = upstreamPort;
        Port = port;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public int Port { get; }

    public string Endpoint => $"http://127.0.0.1:{Port}";

    public bool OperatorDrives => _operatorDrives;

    // The port is picked by binding and releasing one, so a start that loses the race to another process retries.
    public static AgentCdpProxy Start(int upstreamPort)
    {
        for (var attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var proxy = new AgentCdpProxy($"http://127.0.0.1:{upstreamPort}", upstreamPort, port);
            try
            {
                proxy._listener.Start();
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                continue;
            }

            _ = proxy.AcceptLoopAsync();
            return proxy;
        }
    }

    public void TakeOver() => _operatorDrives = true;

    public void Release() => _operatorDrives = false;

    // The browser is gone: new calls get the reason, and the sockets already open are closed with it.
    public void MarkStopped()
    {
        _stopped = true;
        CloseSockets(WebSocketCloseStatus.EndpointUnavailable, StoppedMessage);
    }

    public void Dispose()
    {
        _stopped = true;
        _lifetime.Cancel();
        CloseSockets(WebSocketCloseStatus.EndpointUnavailable, StoppedMessage);
        try
        {
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void CloseSockets(WebSocketCloseStatus status, string description)
    {
        foreach (var socket in _sockets.Keys)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await socket.CloseOutputAsync(status, description, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException or InvalidOperationException)
                {
                    // Already closed.
                }
            });
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (_lifetime.IsCancellationRequested == false)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            if (_stopped)
            {
                await WriteAsync(context.Response, 503, StoppedMessage).ConfigureAwait(false);
            }
            else if (context.Request.IsWebSocketRequest)
            {
                await ProxySocketAsync(context).ConfigureAwait(false);
            }
            else
            {
                await ProxyHttpAsync(context).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or HttpListenerException or WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            try
            {
                context.Response.Abort();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private async Task ProxyHttpAsync(HttpListenerContext context)
    {
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.HttpMethod), _upstream + context.Request.RawUrl);
        if (context.Request.HasEntityBody)
        {
            request.Content = new StreamContent(context.Request.InputStream);
        }

        using var response = await Http.SendAsync(request, _lifetime.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(_lifetime.Token).ConfigureAwait(false);

        // Chromium names itself in the socket URLs it hands out; the client must be sent back through here.
        body = body.Replace($"127.0.0.1:{_upstreamPort}", $"127.0.0.1:{Port}", StringComparison.Ordinal)
            .Replace($"localhost:{_upstreamPort}", $"127.0.0.1:{Port}", StringComparison.Ordinal);
        await WriteAsync(context.Response, (int)response.StatusCode, body, response.Content.Headers.ContentType?.ToString()).ConfigureAwait(false);
    }

    private static async Task WriteAsync(HttpListenerResponse response, int status, string body, string? contentType = null)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = status;
        response.ContentType = contentType ?? "text/plain; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }

    private async Task ProxySocketAsync(HttpListenerContext context)
    {
        using var upstream = new ClientWebSocket();
        await upstream.ConnectAsync(new Uri($"ws://127.0.0.1:{_upstreamPort}{context.Request.RawUrl}"), _lifetime.Token).ConfigureAwait(false);
        var accepted = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
        var client = accepted.WebSocket;
        _sockets[client] = 0;
        try
        {
            var toBrowser = PumpAsync(client, upstream, fromClient: true, client);
            var toClient = PumpAsync(upstream, client, fromClient: false, client);
            await Task.WhenAny(toBrowser, toClient).ConfigureAwait(false);
        }
        finally
        {
            _sockets.TryRemove(client, out _);
            client.Dispose();
        }
    }

    // Whole messages, so a command can be read before it is passed on. `Input.` is looked for as text first: most
    // traffic never needs parsing.
    private async Task PumpAsync(WebSocket source, WebSocket target, bool fromClient, WebSocket client)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (source.State == WebSocketState.Open && _lifetime.IsCancellationRequested == false)
        {
            message.SetLength(0);
            WebSocketReceiveResult received;
            do
            {
                received = await source.ReceiveAsync(buffer, _lifetime.Token).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                message.Write(buffer, 0, received.Count);
            }
            while (received.EndOfMessage == false);

            var bytes = message.GetBuffer().AsMemory(0, (int)message.Length);
            if (fromClient && received.MessageType == WebSocketMessageType.Text && _operatorDrives && Refusal(bytes.Span) is { } refusal)
            {
                await client.SendAsync(refusal, WebSocketMessageType.Text, true, _lifetime.Token).ConfigureAwait(false);
                continue;
            }

            await target.SendAsync(bytes, received.MessageType, true, _lifetime.Token).ConfigureAwait(false);
        }
    }

    // The error reply to an input command of the session, or null when the message is none.
    private static byte[]? Refusal(ReadOnlySpan<byte> message)
    {
        if (message.IndexOf("\"Input."u8) < 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(message.ToArray());
            var root = document.RootElement;
            if (root.TryGetProperty("method", out var method) == false
                || method.GetString() is not { } name
                || name.StartsWith("Input.", StringComparison.Ordinal) == false
                || root.TryGetProperty("id", out var id) == false)
            {
                return null;
            }

            var reply = new Dictionary<string, object?>
            {
                ["id"] = id.Clone(),
                ["error"] = new { code = -32000, message = TakenOverMessage },
            };
            if (root.TryGetProperty("sessionId", out var session))
            {
                reply["sessionId"] = session.Clone();
            }

            return JsonSerializer.SerializeToUtf8Bytes(reply);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
