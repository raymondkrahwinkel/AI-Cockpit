using System.Collections.Concurrent;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;
using Cockpit.TestSupport;
using Microsoft.AspNetCore.Http;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// The remote-auth path of <see cref="PluginStoreClient"/> (AC-7): a private store's token rides along as a
/// bearer header to the store's own origin, a public store sends none, and the token is never attached to an
/// absolute icon or zip URL on a foreign host — a credential belongs only on a request to the store's own host.
/// </summary>
public class PluginStoreClientRemoteAuthTests : IAsyncLifetime
{
    private readonly ConcurrentDictionary<string, string?> _authByPath = new();
    private readonly PluginStoreClient _client = new();
    private LoopbackHttpServer? _store;
    private LoopbackHttpServer? _foreign;
    private string _prefix = string.Empty;
    private string _foreignPrefix = string.Empty;

    public async Task InitializeAsync()
    {
        _store = await LoopbackHttpServer.StartAsync(_RecordAndAnswerAsync);
        _foreign = await LoopbackHttpServer.StartAsync(_RecordAndAnswerAsync);
        _prefix = _store.BaseUrl;
        _foreignPrefix = _foreign.BaseUrl;
    }

    [Fact]
    public async Task FetchIndexAsync_PrivateStore_SendsBearerToken()
    {
        var store = PluginStoreConfig.Remote($"{_prefix}mystore/", "s3cr3t");

        var result = await _client.FetchIndexAsync(store);

        Assert.True(result.IsSuccess);
        Assert.Equal("Bearer s3cr3t", _authByPath["/mystore/index.json"]);
    }

    [Fact]
    public async Task FetchIndexAsync_PublicStore_SendsNoAuthorization()
    {
        var store = PluginStoreConfig.Remote($"{_prefix}public/");

        var result = await _client.FetchIndexAsync(store);

        Assert.True(result.IsSuccess);
        Assert.Null(_authByPath["/public/index.json"]);
    }

    [Fact]
    public async Task DownloadZipAsync_SameOrigin_SendsTheToken()
    {
        var store = PluginStoreConfig.Remote($"{_prefix}mystore/", "s3cr3t");

        await _client.DownloadZipAsync(store, "plugin.zip", null);

        Assert.Equal("Bearer s3cr3t", _authByPath["/mystore/plugin.zip"]);
    }

    [Fact]
    public async Task DownloadZipAsync_AbsolutePathToForeignHost_DoesNotSendTheToken()
    {
        // A store-controlled index that lists a zip on another origin must not exfiltrate the token.
        var store = PluginStoreConfig.Remote($"{_prefix}mystore/", "s3cr3t");

        await _client.DownloadZipAsync(store, $"{_foreignPrefix}evil.zip", null);

        Assert.Null(_authByPath["/evil.zip"]);
    }

    [Fact]
    public async Task DownloadImageAsync_AbsoluteIconUrl_DoesNotLeakTheToken()
    {
        var store = PluginStoreConfig.Remote($"{_prefix}mystore/", "s3cr3t");

        await _client.DownloadImageAsync(store, $"{_foreignPrefix}logo.png");

        Assert.Null(_authByPath["/logo.png"]);
    }

    [Fact]
    public async Task DownloadZipAsync_PrivateGitHub_RejectsATraversalPath()
    {
        // No network: an unsafe path is refused before any request is built.
        var store = PluginStoreConfig.Remote("https://github.com/owner/repo", "token");

        var result = await _client.DownloadZipAsync(store, "../../other/repo/x.zip", null);

        Assert.False(result.IsSuccess);
        Assert.Contains("unsafe", result.Error);
    }

    private async Task _RecordAndAnswerAsync(HttpContext context)
    {
        // Absent and present-but-empty are recorded apart: "sent no header" is the assertion the token tests make,
        // and an empty header would still be a header the client chose to attach.
        _authByPath[context.Request.Path.ToString()] =
            context.Request.Headers.TryGetValue("Authorization", out var authorization) ? authorization.ToString() : null;

        await context.Response.WriteAsync("""{ "name": "s", "plugins": [] }""");
    }

    public async Task DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        if (_foreign is not null)
        {
            await _foreign.DisposeAsync();
        }
    }
}
