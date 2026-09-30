using Cockpit.Infrastructure.Plugins;

namespace Cockpit.Backend.Tests.Plugins;

/// <summary>The in-memory + write-through per-plugin key/value store behind IPluginStorage (#14).</summary>
public class PluginStorageTests
{
    [Fact]
    public void SetThenGet_RoundTripsTypedValues()
    {
        var storage = new PluginStorage(new Dictionary<string, string>(), _ => { });

        storage.Set("token", "ghp_secret");
        storage.Set("count", 42);

        Assert.Equal("ghp_secret", storage.Get<string>("token"));
        Assert.Equal(42, storage.Get<int>("count"));
    }

    [Fact]
    public void Set_WritesThroughToPersist()
    {
        IReadOnlyDictionary<string, string>? persisted = null;
        var storage = new PluginStorage(new Dictionary<string, string>(), values => persisted = values);

        storage.Set("k", "v");

        Assert.NotNull(persisted);
        Assert.Contains("k", persisted.Keys);
    }

}
