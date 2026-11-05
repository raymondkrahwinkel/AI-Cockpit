using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Cockpit.Plugin.Browser;

// SOCKS5 keeps HTTP, TLS and WebSocket framing in Chromium. Only the proxy resolves and connects to destinations.
internal sealed class AgentNetworkProxy : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<Socket, byte> _clients = new();
    private readonly Func<AddressPolicy> _policy;

    public AgentNetworkProxy(Func<AddressPolicy> policy)
    {
        _policy = policy;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptAsync();
    }

    public int Port { get; }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var client = await _listener.AcceptSocketAsync(_lifetime.Token).ConfigureAwait(false);
                _clients[client] = 0;
                _ = HandleAsync(client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // A closed listener fails closed; Chromium has no direct fallback.
            Dispose();
        }
    }

    private async Task HandleAsync(Socket client)
    {
        using (client)
        using (var downstream = new NetworkStream(client, ownsSocket: false))
        using (var upstream = new Socket(SocketType.Stream, ProtocolType.Tcp))
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
        {
            _clients[upstream] = 0;
            try
            {
                handshake.CancelAfter(TimeSpan.FromSeconds(15));
                var token = handshake.Token;
                var greeting = new byte[2];
                await downstream.ReadExactlyAsync(greeting, token).ConfigureAwait(false);
                if (greeting[0] != 5 || greeting[1] == 0)
                {
                    return;
                }
                var methods = new byte[greeting[1]];
                await downstream.ReadExactlyAsync(methods, token).ConfigureAwait(false);
                if (!methods.Contains((byte)0))
                {
                    await downstream.WriteAsync(new byte[] { 5, 255 }, token).ConfigureAwait(false);
                    return;
                }
                await downstream.WriteAsync(new byte[] { 5, 0 }, token).ConfigureAwait(false);
                var request = new byte[4];
                await downstream.ReadExactlyAsync(request, token).ConfigureAwait(false);
                if (request[0] != 5 || request[1] != 1 || request[2] != 0)
                {
                    await ReplyAsync(downstream, 7, token).ConfigureAwait(false);
                    return;
                }
                var host = await ReadHostAsync(downstream, request[3], token).ConfigureAwait(false);
                var portBytes = new byte[2];
                await downstream.ReadExactlyAsync(portBytes, token).ConfigureAwait(false);
                var port = portBytes[0] * 256 + portBytes[1];
                if (host is null || port == 0 || !Uri.TryCreate($"http://{host}:{port}/", UriKind.Absolute, out var destination))
                {
                    await ReplyAsync(downstream, 8, token).ConfigureAwait(false);
                    return;
                }
                var resolved = await _policy().ResolveAsync(destination, token).ConfigureAwait(false);
                if (resolved.Reason is not null)
                {
                    await ReplyAsync(downstream, 2, token).ConfigureAwait(false);
                    return;
                }
                await ReplyAsync(downstream, 0, token).ConfigureAwait(false);
                var initial = await ReadInitialAsync(downstream, token).ConfigureAwait(false);
                if (_policy().IsOwnMachineOnly(resolved.Addresses) && IsForeignWebSocket(initial))
                {
                    return;
                }
                // This overload uses only checked numeric addresses, including fallback; no second DNS lookup can rebind.
                await upstream.ConnectAsync(resolved.Addresses, port, token).ConfigureAwait(false);
                using var remote = new NetworkStream(upstream, ownsSocket: false);
                using var relay = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                await remote.WriteAsync(initial, relay.Token).ConfigureAwait(false);
                var sending = downstream.CopyToAsync(remote, relay.Token);
                var receiving = remote.CopyToAsync(downstream, relay.Token);
                await Task.WhenAny(sending, receiving).ConfigureAwait(false);
                await relay.CancelAsync().ConfigureAwait(false);
                await Task.WhenAll(sending, receiving).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Refusal, peer disconnect and browser shutdown all close both ends without retrying by hostname.
            }
            finally
            {
                _clients.TryRemove(client, out _);
                _clients.TryRemove(upstream, out _);
            }
        }
    }

    private static async Task<string?> ReadHostAsync(Stream stream, byte type, CancellationToken token)
    {
        var length = type switch { 1 => 4, 4 => 16, _ => 0 };
        if (type == 3)
        {
            var size = new byte[1];
            await stream.ReadExactlyAsync(size, token).ConfigureAwait(false);
            var name = new byte[size[0]];
            await stream.ReadExactlyAsync(name, token).ConfigureAwait(false);
            var host = Encoding.ASCII.GetString(name);
            return Uri.CheckHostName(host) switch
            {
                UriHostNameType.Dns or UriHostNameType.IPv4 => host,
                UriHostNameType.IPv6 => $"[{host.Trim('[', ']')}]",
                _ => null,
            };
        }
        if (length == 0)
        {
            return null;
        }
        var address = new byte[length];
        await stream.ReadExactlyAsync(address, token).ConfigureAwait(false);
        var ip = new IPAddress(address);
        return type == 4 ? $"[{ip}]" : ip.ToString();
    }

    private static async Task<byte[]> ReadInitialAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
        return buffer[..read];
    }

    private bool IsForeignWebSocket(byte[] initial)
    {
        var request = Encoding.ASCII.GetString(initial);
        if (!request.Contains("\r\nUpgrade: websocket", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var origin = request.Split("\r\n", StringSplitOptions.None).FirstOrDefault(line => line.StartsWith("Origin:", StringComparison.OrdinalIgnoreCase))?[7..].Trim();
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return true;
        }
        var resolved = _policy().ResolveAsync(uri, _lifetime.Token).GetAwaiter().GetResult();
        return resolved.Reason is not null || !_policy().IsOwnMachineOnly(resolved.Addresses);
    }

    private static ValueTask ReplyAsync(Stream stream, byte status, CancellationToken token) =>
        stream.WriteAsync(new byte[] { 5, status, 0, 1, 0, 0, 0, 0, 0, 0 }, token);

    public void Dispose()
    {
        _lifetime.Cancel();
        _listener.Stop();
        foreach (var client in _clients.Keys)
        {
            client.Dispose();
        }
    }
}
