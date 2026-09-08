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
            // AC-1284: 0 rather than a plausible port number, because 0 is the value the on-disk shape lost
            // silently — a Port that never round-tripped read back as the default and bound that instead.
            Port = 0,
            AllowedDiscoveryRanges = ["203.0.113.0/24", "198.51.100.0/24"],
        });

        var reloaded = await new NodeEndpointSettingsStore(_path).LoadAsync();
        Assert.True(reloaded.Enabled);
        Assert.Equal("test-secret-value", reloaded.SharedSecret);
        Assert.Equal(0, reloaded.Port);
        Assert.Equal(["203.0.113.0/24", "198.51.100.0/24"], reloaded.AllowedDiscoveryRanges);
    }

    /// <summary>
    /// AC-1292 criterion 4: a pairing written before the "all" flags existed says nothing about them, and that
    /// silence must read as the reach it already had. Reading an absent flag as the new default would hand every
    /// coupling made under AC-794 the whole cockpit at the next launch.
    /// </summary>
    [Fact]
    public async Task Load_PairingWrittenBeforeTheAllFlagsExisted_KeepsExactlyItsListedReach()
    {
        await File.WriteAllTextAsync(
            _path,
            """
            {
              "NodeEndpoint": {
                "Enabled": true,
                "SharedSecret": "minted-before-this-change",
                "Pairing": {
                  "ControllerName": "desk",
                  "ControllerAddress": "192.168.1.5",
                  "PairedAtUtc": "2026-08-15T12:00:00+00:00",
                  "AllowedProfileLabels": [ "default" ],
                  "AllowedProjectIds": [ "proj-1" ]
                }
              }
            }
            """);

        var pairing = (await new NodeEndpointSettingsStore(_path).LoadAsync()).Pairing!;

        Assert.False(pairing.AllowAllProfiles);
        Assert.False(pairing.AllowAllProjects);
        Assert.Equal(["default"], pairing.AllowedProfileLabels);
        Assert.Equal(["proj-1"], pairing.AllowedProjectIds);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + "*"))
        {
            File.Delete(file);
        }
    }
}
