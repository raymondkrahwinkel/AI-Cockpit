using System.Text.Json;
using Cockpit.Core.Plugins;
using FluentAssertions;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>The string-or-object JSON shape of a <see cref="PluginStoreConfig"/> (AC-7): a bare URL string from a pre-AC-7 config still reads, an object round-trips, and the token is written under the secret-scrubbed name.</summary>
public class PluginStoreConfigJsonConverterTests
{
    [Fact]
    public void Read_BareString_IsPublicRemote()
    {
        var store = JsonSerializer.Deserialize<PluginStoreConfig>("\"https://github.com/a/b\"");

        store.Should().Be(PluginStoreConfig.Remote("https://github.com/a/b"));
        store!.HasToken.Should().BeFalse();
    }

    [Fact]
    public void Read_RemoteObjectWithToken_KeepsTheToken()
    {
        var store = JsonSerializer.Deserialize<PluginStoreConfig>(
            """{ "kind": "remote", "location": "https://github.com/a/b", "token": "abc123" }""");

        store.Should().Be(new PluginStoreConfig(PluginStoreKind.Remote, "https://github.com/a/b", "abc123"));
    }

    [Fact]
    public void Read_LocalObject_HasLocalKind()
    {
        var store = JsonSerializer.Deserialize<PluginStoreConfig>(
            """{ "kind": "local", "location": "/home/raymond/plugins" }""");

        store!.Kind.Should().Be(PluginStoreKind.Local);
        store.Location.Should().Be("/home/raymond/plugins");
    }

    [Fact]
    public void Read_UrlOrPathAlias_ResolvesToLocation()
    {
        JsonSerializer.Deserialize<PluginStoreConfig>("""{ "url": "https://x/index.json" }""")!
            .Location.Should().Be("https://x/index.json");
        JsonSerializer.Deserialize<PluginStoreConfig>("""{ "kind": "local", "path": "/tmp/store" }""")!
            .Location.Should().Be("/tmp/store");
    }

    [Fact]
    public void Write_RemoteWithToken_EmitsObjectWithTokenField()
    {
        var json = JsonSerializer.Serialize(PluginStoreConfig.Remote("https://github.com/a/b", "abc123"));

        json.Should().Contain("\"kind\":\"remote\"");
        json.Should().Contain("\"location\":\"https://github.com/a/b\"");
        // The field must be named "token" so the host's secret layer encrypts it at rest and scrubs it from backups.
        json.Should().Contain("\"token\":\"abc123\"");
    }

    [Fact]
    public void Write_RemoteWithoutToken_OmitsTokenField()
    {
        JsonSerializer.Serialize(PluginStoreConfig.Remote("https://github.com/a/b"))
            .Should().NotContain("token");
    }

    [Fact]
    public void RoundTrip_LocalStore_IsStable()
    {
        var original = PluginStoreConfig.Local("/home/raymond/plugins");

        var restored = JsonSerializer.Deserialize<PluginStoreConfig>(JsonSerializer.Serialize(original));

        restored.Should().Be(original);
    }

    [Fact]
    public void ToString_RedactsToken()
    {
        PluginStoreConfig.Remote("https://github.com/a/b", "s3cr3t").ToString()
            .Should().NotContain("s3cr3t").And.Contain("***");
    }
}
