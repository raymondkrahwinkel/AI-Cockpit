using Cockpit.App.Plugins;
using FluentAssertions;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>The in-memory + write-through per-plugin key/value store behind IPluginStorage (#14).</summary>
public class PluginStorageTests
{
    [Fact]
    public void SetThenGet_RoundTripsTypedValues()
    {
        var storage = new PluginStorage(new Dictionary<string, string>(), _ => { });

        storage.Set("token", "ghp_secret");
        storage.Set("count", 42);

        storage.Get<string>("token").Should().Be("ghp_secret");
        storage.Get<int>("count").Should().Be(42);
    }

    [Fact]
    public void Get_MissingKey_ReturnsDefault()
    {
        var storage = new PluginStorage(new Dictionary<string, string>(), _ => { });

        storage.Get<string>("nope").Should().BeNull();
        storage.Get<int>("nope").Should().Be(0);
    }

    [Fact]
    public void SeededValues_AreReadable()
    {
        var storage = new PluginStorage(new Dictionary<string, string> { ["repo"] = "\"owner/name\"" }, _ => { });

        storage.Get<string>("repo").Should().Be("owner/name");
    }

    [Fact]
    public void Set_WritesThroughToPersist()
    {
        IReadOnlyDictionary<string, string>? persisted = null;
        var storage = new PluginStorage(new Dictionary<string, string>(), values => persisted = values);

        storage.Set("k", "v");

        persisted.Should().NotBeNull();
        persisted!.Should().ContainKey("k");
    }
}
