using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;
using FluentAssertions;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// The remote-auth path of <see cref="PluginStoreClient"/> (AC-7): a private store's token rides along as a
/// bearer header, a public store sends none, and the token is never attached to an absolute icon URL — a
/// credential belongs only on a request to the store's own host.
/// </summary>
public class PluginStoreClientRemoteAuthTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _prefix;
    private readonly ConcurrentDictionary<string, string?> _authByPath = new();
    private readonly PluginStoreClient _client = new();

    public PluginStoreClientRemoteAuthTests()
    {
        var port = _FreePort();
        _prefix = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(_prefix);
        _listener.Start();
        _ = _ServeAsync();
    }

    [Fact]
    public async Task FetchIndexAsync_PrivateStore_SendsBearerToken()
    {
        var store = PluginStoreConfig.Remote($"{_prefix}mystore/", "s3cr3t");

        var result = await _client.FetchIndexAsync(store);

        result.IsSuccess.Should().BeTrue();
        _authByPath["/mystore/index.json"].Should().Be("Bearer s3cr3t");
    }

    [Fact]
    public async Task FetchIndexAsync_PublicStore_SendsNoAuthorization()
    {
        var store = PluginStoreConfig.Remote($"{_prefix}public/");

        var result = await _client.FetchIndexAsync(store);

        result.IsSuccess.Should().BeTrue();
        _authByPath["/public/index.json"].Should().BeNull();
    }

    [Fact]
    public async Task DownloadImageAsync_AbsoluteIconUrl_DoesNotLeakTheToken()
    {
        // Even for a private store, an absolute icon URL is fetched without the token — it may point at any host.
        var store = PluginStoreConfig.Remote($"{_prefix}mystore/", "s3cr3t");

        await _client.DownloadImageAsync(store, $"{_prefix}cdn/logo.png");

        _authByPath["/cdn/logo.png"].Should().BeNull();
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
                return; // listener stopped
            }

            _authByPath[context.Request.Url!.AbsolutePath] = context.Request.Headers["Authorization"];

            var body = Encoding.UTF8.GetBytes("""{ "name": "s", "plugins": [] }""");
            context.Response.StatusCode = 200;
            await context.Response.OutputStream.WriteAsync(body);
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
