using Cockpit.Plugin.Kubernetes.Kind;
using Cockpit.Plugin.Kubernetes.Model;
using Cockpit.Plugin.Kubernetes.Settings;

namespace Cockpit.Plugin.Kubernetes.Tests;

// AC-576 phase 3, AC 8: the Argo token must go through the same secret layer as the kubeconfig — never as
// plain metadata, which is what ends up in cockpit.json.
public class KubernetesSettingsTests
{
    [Fact]
    public void GetArgoToken_NoneSet_ReturnsNull()
    {
        var settings = new KubernetesSettings(new FakePluginStorage());

        Assert.Null(settings.GetArgoToken("cluster-1"));
    }

    [Fact]
    public void SetArgoToken_RoundTrips_ThroughTheSecretLayer()
    {
        var storage = new FakePluginStorage();
        var settings = new KubernetesSettings(storage);

        settings.SetArgoToken("cluster-1", "argocd-token-value");

        Assert.Equal("argocd-token-value", settings.GetArgoToken("cluster-1"));
        Assert.Contains("cluster.cluster-1.argoToken", storage.SecretKeys);
    }

    [Fact]
    public void ClearArgoToken_RemovesIt()
    {
        var settings = new KubernetesSettings(new FakePluginStorage());
        settings.SetArgoToken("cluster-1", "argocd-token-value");

        settings.ClearArgoToken("cluster-1");

        Assert.Null(settings.GetArgoToken("cluster-1"));
    }

    [Fact]
    public void SetArgoToken_IsKeyedPerCluster()
    {
        var settings = new KubernetesSettings(new FakePluginStorage());
        settings.SetArgoToken("cluster-1", "token-for-1");
        settings.SetArgoToken("cluster-2", "token-for-2");

        Assert.Equal("token-for-1", settings.GetArgoToken("cluster-1"));
        Assert.Equal("token-for-2", settings.GetArgoToken("cluster-2"));
    }

    // AC-576 phase 3, AC 8: `Clusters` is what `KubernetesSettings.Clusters` serializes into cockpit.json — a
    // token field on `ClusterRegistration` would put it there. There is none, structurally, not by convention.
    [Fact]
    public void ClusterRegistration_HasNoTokenField()
    {
        var tokenLikeProperties = typeof(ClusterRegistration).GetProperties()
            .Where(property => property.Name.Contains("Token", StringComparison.OrdinalIgnoreCase));

        Assert.Empty(tokenLikeProperties);
    }

    // AC-179 criterion 3/8/11: the kind-cluster registry round-trips through the same Get<List<T>>/Set idiom as
    // `Clusters` — no separate storage mechanism needed.
    [Fact]
    public void KindClusters_StartEmpty_ThenRoundTrip()
    {
        var settings = new KubernetesSettings(new FakePluginStorage());
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
        var settings = new KubernetesSettings(new FakePluginStorage());

        Assert.Equal(TimeSpan.FromHours(4), settings.KindClusterMaxLifetime);

        settings.KindClusterMaxLifetime = TimeSpan.FromHours(8);

        Assert.Equal(TimeSpan.FromHours(8), settings.KindClusterMaxLifetime);
    }
}
