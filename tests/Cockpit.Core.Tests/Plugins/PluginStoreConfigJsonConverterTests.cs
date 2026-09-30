using System.Text.Json;
using Cockpit.Core.Plugins;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>The string-or-object JSON shape of a <see cref="PluginStoreConfig"/> (AC-7): a bare URL string from a pre-AC-7 config still reads, an object round-trips, and the token is written under the secret-scrubbed name.</summary>
public class PluginStoreConfigJsonConverterTests
{
    [Fact]
    public void Read_BareString_IsPublicRemote()
    {
        var store = JsonSerializer.Deserialize<PluginStoreConfig>("\"https://github.com/a/b\"");

        Assert.Equal(PluginStoreConfig.Remote("https://github.com/a/b"), store);
        Assert.False(store!.HasToken);
    }

    [Fact]
    public void Read_RemoteObjectWithToken_KeepsTheToken()
    {
        var store = JsonSerializer.Deserialize<PluginStoreConfig>(
            """{ "kind": "remote", "location": "https://github.com/a/b", "token": "abc123" }""");

        Assert.Equal(new PluginStoreConfig(PluginStoreKind.Remote, "https://github.com/a/b", "abc123"), store);
    }

    [Fact]
    public void Read_UrlOrPathAlias_ResolvesToLocation()
    {
        Assert.Equal("https://x/index.json", JsonSerializer.Deserialize<PluginStoreConfig>("""{ "url": "https://x/index.json" }""")!.Location);
        Assert.Equal("/tmp/store", JsonSerializer.Deserialize<PluginStoreConfig>("""{ "kind": "local", "path": "/tmp/store" }""")!.Location);
    }

    [Fact]
    public void RoundTrip_LocalStore_IsStable()
    {
        var original = PluginStoreConfig.Local("/home/raymond/plugins");

        var restored = JsonSerializer.Deserialize<PluginStoreConfig>(JsonSerializer.Serialize(original));

        Assert.Equal(original, restored);
    }

    [Fact]
    public void ToString_RedactsToken()
    {
        var text = PluginStoreConfig.Remote("https://github.com/a/b", "s3cr3t").ToString();
        Assert.DoesNotContain("s3cr3t", text);
        Assert.Contains("***", text);
    }
}
