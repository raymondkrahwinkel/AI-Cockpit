using Cockpit.Plugin.Kind.Settings;

namespace Cockpit.Plugin.Kind.Tests;

// AC-179 criterion 3/8/11: the registry and the TTL that the three sweeps read from.
public class KindSettingsTests
{
    [Fact]
    public void KindClusters_StartEmpty_ThenRoundTrip()
    {
        var settings = new KindSettings(new FakePluginStorage());
        var record = new KindClusterRecord("cockpit-ac179", "pane-1", "/state/kind/cockpit-ac179.kubeconfig", DateTimeOffset.UtcNow);

        Assert.Empty(settings.KindClusters);

        settings.KindClusters = [record];

        Assert.Single(settings.KindClusters);
        Assert.Equal(record, settings.KindClusters[0]);
    }

    [Fact]
    public void KindClusterMaxLifetime_DefaultsToFourHours_ThenRoundTrips()
    {
        // The default is the reaper's own budget, not a formatting detail: a kind cluster nobody deleted is torn
        // down after it, so a change to it changes how long a stray cluster keeps running.
        var settings = new KindSettings(new FakePluginStorage());

        Assert.Equal(TimeSpan.FromHours(4), settings.KindClusterMaxLifetime);

        settings.KindClusterMaxLifetime = TimeSpan.FromHours(8);

        Assert.Equal(TimeSpan.FromHours(8), settings.KindClusterMaxLifetime);
    }
}
