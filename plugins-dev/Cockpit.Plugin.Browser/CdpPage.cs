using System.Net.WebSockets;
using System.Text.Json;

namespace Cockpit.Plugin.Browser;

// AC-1713: One CDP connection of ours to one tab of an agent browser, straight to Chromium (not through the pause
// proxy: Raymond's own input and the screencast must not be held by it). Commands are fired and forgotten; only events
// are read back.
internal sealed class CdpPage : IDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private int _nextId;

    private CdpPage()
    {
    }

    public static async Task<CdpPage> ConnectAsync(string socketUrl, Action<string, JsonElement> onEvent, CancellationToken cancellationToken)
    {
        var page = new CdpPage();
        await page._socket.ConnectAsync(new Uri(socketUrl), cancellationToken).ConfigureAwait(false);
        _ = page.ReadAsync(onEvent);
        return page;
    }

    public async Task SendAsync(string method, object? parameters = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { id = Interlocked.Increment(ref _nextId), method, @params = parameters ?? new { } });
        await _sendGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, _lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _socket.Dispose();
    }

    private async Task ReadAsync(Action<string, JsonElement> onEvent)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult received;
                do
                {
                    received = await _socket.ReceiveAsync(buffer, _lifetime.Token).ConfigureAwait(false);
                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    message.Write(buffer, 0, received.Count);
                }
                while (received.EndOfMessage == false);

                using var document = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
                var root = document.RootElement;
                if (root.TryGetProperty("method", out var method) && method.GetString() is { } name)
                {
                    onEvent(name, root.TryGetProperty("params", out var parameters) ? parameters : default);
                }
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException or JsonException)
        {
            // The tab or the browser is gone: nothing more to read.
        }
    }
}
