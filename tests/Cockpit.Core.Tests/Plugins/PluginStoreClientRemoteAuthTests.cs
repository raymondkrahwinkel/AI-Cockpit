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
/// bearer header to the store's own origin, a public store sends none, and the token is never attached to an
/// absolute icon or zip URL on a foreign host — a credential belongs only on a request to the store's own host.
/// </summary>
public class PluginStoreClientRemoteAuthTests : IDisposable
{
    private readonly HttpListener _store = new();
    private readonly HttpListener _foreign = new();
    private readonly string _prefix;
    private readonly string _foreignPrefix;
    private readonly ConcurrentDictionary<string, string?> _authByPath = new();
    private readonly PluginStoreClient _client = new();

    public PluginStoreClientRemoteAuthTests()
    {
        _prefix = _StartListener(_store);
        _foreignPrefix = _StartListener(_foreign);
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
    public async Task DownloadZipAsync_SameOrigin_SendsTheToken()
    {
        var store = PluginStoreConfig.Remote($"{_prefix}mystore/", "s3cr3t");

        await _client.DownloadZipAsync(store, "plugin.zip", null);

        _authByPath["/mystore/plugin.zip"].Should().Be("Bearer s3cr3t");
    }

    [Fact]
    public async Task DownloadZipAsync_AbsolutePathToForeignHost_DoesNotSendTheToken()
    {
        // A store-controlled index that lists a zip on another origin must not exfiltrate the token.
        var store = PluginStoreConfig.Remote($"{_prefix}mystore/", "s3cr3t");

        await _client.DownloadZipAsync(store, $"{_foreignPrefix}evil.zip", null);

        _authByPath["/evil.zip"].Should().BeNull();
    }

    [Fact]
    public async Task DownloadImageAsync_AbsoluteIconUrl_DoesNotLeakTheToken()
    {
        var store = PluginStoreConfig.Remote($"{_prefix}mystore/", "s3cr3t");

        await _client.DownloadImageAsync(store, $"{_foreignPrefix}logo.png");

        _authByPath["/logo.png"].Should().BeNull();
    }

    [Fact]
    public async Task DownloadZipAsync_PrivateGitHub_RejectsATraversalPath()
    {
        // No network: an unsafe path is refused before any request is built.
        var store = PluginStoreConfig.Remote("https://github.com/owner/repo", "token");

        var result = await _client.DownloadZipAsync(store, "../../other/repo/x.zip", null);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("unsafe");
    }

    private string _StartListener(HttpListener listener)
    {
        var prefix = $"http://127.0.0.1:{_FreePort()}/";
        listener.Prefixes.Add(prefix);
        listener.Start();
        _ = _ServeAsync(listener);

        return prefix;
    }

    private async Task _ServeAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
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
        foreach (var listener in new[] { _store, _foreign })
        {
            if (listener.IsListening)
            {
                listener.Stop();
            }

            listener.Close();
        }
    }
}
