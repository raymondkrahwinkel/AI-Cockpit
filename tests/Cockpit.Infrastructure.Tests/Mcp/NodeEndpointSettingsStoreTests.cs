using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Tests.Mcp;

/// <summary>The network-node master switch and its shared secret persist across restarts, and a config that never saved either defaults to off with an empty secret (AC-790).</summary>
public class NodeEndpointSettingsStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"node-endpoint-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task Load_WhenNothingSaved_DefaultsToOffWithNoSecretAndNoWhitelist()
    {
        var store = new NodeEndpointSettingsStore(_path);

        var settings = await store.LoadAsync();

        // Off is the deliberate answer for a config that never saved one: the node endpoint is a way in from the
        // network, and an install that predates this setting must not come up listening.
        Assert.False(settings.Enabled);
        Assert.Equal("", settings.SharedSecret);
        Assert.Empty(settings.AllowedDiscoveryRanges);
    }

    [Fact]
    public async Task Save_ThenLoad_RoundTripsEverySettingThroughItsOnDiskShape()
    {
        var store = new NodeEndpointSettingsStore(_path);

        await store.SaveAsync(new NodeEndpointSettings
        {
            Enabled = true,
            SharedSecret = "test-secret-value",
            AllowedDiscoveryRanges = ["203.0.113.0/24", "198.51.100.0/24"],
        });

        var reloaded = await new NodeEndpointSettingsStore(_path).LoadAsync();
        Assert.True(reloaded.Enabled);
        Assert.Equal("test-secret-value", reloaded.SharedSecret);
        Assert.Equal(["203.0.113.0/24", "198.51.100.0/24"], reloaded.AllowedDiscoveryRanges);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + "*"))
        {
            File.Delete(file);
        }
    }
}
