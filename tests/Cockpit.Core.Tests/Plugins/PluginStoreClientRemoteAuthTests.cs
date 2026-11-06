using System.Collections.Concurrent;
using Cockpit.Core.Abstractions.Plugins;
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
    private readonly List<string?> _downloads = [];
    private readonly PluginStoreClient _client = new();
    private LoopbackHttpServer? _store;
    private LoopbackHttpServer? _foreign;
    private string _prefix = string.Empty;
    private string _foreignPrefix = string.Empty;
    private string _indexJson = """{ "name": "s", "plugins": [] }""";

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
    public async Task DownloadZipOrImage_AbsoluteUrlToForeignHost_DoesNotSendTheToken()
    {
        // A store-controlled index that lists a zip or an icon on another origin must not exfiltrate the token.
        var store = PluginStoreConfig.Remote($"{_prefix}mystore/", "s3cr3t");

        await _client.DownloadZipAsync(store, $"{_foreignPrefix}evil.zip", null);
        await _client.DownloadImageAsync(store, $"{_foreignPrefix}logo.png");

        Assert.Null(_authByPath["/evil.zip"]);
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

    [Theory]
    [InlineData("{foreign}foreign.zip")]
    [InlineData("//{foreignHost}/foreign.zip")]
    public async Task SignedSource_RefreshesCredentialsAndIgnoresUntrustedPaths(string advertisedPath)
    {
        _indexJson = """
            { "name": "s", "iconUrl": "logo.png", "templates": [{ "id": "flow" }],
              "plugins": [{ "id": "tools", "name": "Tools", "latestVersion": "1.2.0",
                "logoAsset": "tools.svg", "versions": [] }] }
            """;
        var token = "first";
        var store = PluginStoreConfig.Remote($"{_prefix}mystore/index.json") with
        {
            Signed = new PluginStoreSignedSource("test:store", "acme", _ => ValueTask.FromResult<string?>(token)),
        };
        IPluginStoreClient client = _client;

        var fetched = await client.FetchIndexAsync(store);
        token = "second";
        var path = advertisedPath.Replace("{foreign}", _foreignPrefix, StringComparison.Ordinal)
            .Replace("{foreignHost}", new Uri(_foreignPrefix).Authority, StringComparison.Ordinal);
        var version = new PluginStoreVersion("1.2.0", path, null, null, null, null);
        var downloaded = await client.DownloadZipAsync(store, version, "tools");
        var image = await client.DownloadImageAsync(store, "logo.png");
        var template = await client.DownloadTemplateAsync(store, "flow.json", null);
        var legacy = await client.DownloadZipAsync(store, path, (string?)null);
        _downloads.Add(downloaded.ZipPath);
        _downloads.Add(legacy.ZipPath);

        Assert.True(fetched.IsSuccess, fetched.Error);
        Assert.NotNull(fetched.Index);
        Assert.Null(fetched.Index.IconUrl);
        Assert.Null(fetched.Index.Templates);
        Assert.Null(Assert.Single(fetched.Index.Plugins).LogoAsset);
        Assert.True(downloaded.IsSuccess, downloaded.Error);
        Assert.DoesNotContain(_authByPath, request => request.Key == "/foreign.zip" && request.Value is not null);
        Assert.Equal("Bearer first", _authByPath["/mystore/index.json"]);
        Assert.Equal("Bearer second", _authByPath["/mystore/plugins/tools/1.2.0/plugin.zip"]);
        Assert.Equal(2, _authByPath.Count);
        Assert.False(image.IsSuccess);
        Assert.False(template.IsSuccess);
        Assert.False(legacy.IsSuccess);
        Assert.NotNull(downloaded.ZipPath);
    }

    private async Task _RecordAndAnswerAsync(HttpContext context)
    {
        // Absent and present-but-empty are recorded apart: "sent no header" is the assertion the token tests make,
        // and an empty header would still be a header the client chose to attach.
        _authByPath[context.Request.Path.ToString()] =
            context.Request.Headers.TryGetValue("Authorization", out var authorization) ? authorization.ToString() : null;

        await context.Response.WriteAsync(_indexJson);
    }

    public async Task DisposeAsync()
    {
        foreach (var download in _downloads)
        {
            if (download is not null)
            {
                File.Delete(download);
            }
        }

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
