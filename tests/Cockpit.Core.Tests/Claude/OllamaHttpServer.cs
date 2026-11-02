using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Cockpit.Core.Tests.Claude;

internal sealed class OllamaHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;
    public string BaseUrl { get; }
    public List<(string Path, JsonElement Body)> Requests { get; } = [];
    public Func<JsonElement, (int Status, string Body)>? Respond { get; set; }
    public TaskCompletionSource? HoldBody { get; set; }
    public TaskCompletionSource BodyHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public OllamaHttpServer()
    {
        _listener.Start();
        BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _run = _RunAsync();
    }

    private async Task _RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var connection = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using var stream = connection.GetStream();
                var header = new List<byte>();
                var one = new byte[1];
                while (await stream.ReadAsync(one, _stop.Token) != 0)
                {
                    header.Add(one[0]);
                    if (header.Count >= 4 && header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                }
                var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                var path = lines[0].Split(' ')[1];
                var length = int.Parse(lines.Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
                var bytes = new byte[length];
                await stream.ReadExactlyAsync(bytes, _stop.Token);
                var body = JsonSerializer.Deserialize<JsonElement>(bytes);
                Requests.Add((path, body));
                var (status, reply) = Respond?.Invoke(body) ?? (200, path == "/api/chat"
                    ? """{"message":{"role":"assistant","content":"hello"},"done":true,"done_reason":"stop","prompt_eval_count":12,"eval_count":3}"""
                    : """{"id":"chat-1","choices":[{"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}]}""");
                var payload = Encoding.UTF8.GetBytes(reply);
                var responseHeader = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nRetry-After: 2\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(responseHeader, _stop.Token);
                if (HoldBody is { } held)
                {
                    BodyHeld.TrySetResult();
                    await held.Task.WaitAsync(_stop.Token);
                }
                await stream.WriteAsync(payload, _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _run;
        _stop.Dispose();
    }
}
