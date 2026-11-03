using Cockpit.Plugin.Kubernetes.Model;
using Cockpit.Plugin.Kubernetes.Settings;

namespace Cockpit.Plugin.Kubernetes.Tests;

// AC-576 phase 3, AC 8: the Argo token must go through the same secret layer as the kubeconfig — never as
// plain metadata, which is what ends up in cockpit.json.
public class KubernetesSettingsTests
{
    [Fact]
    public void SetArgoToken_RoundTrips_ThroughTheSecretLayer_KeyedPerCluster_AndClears()
    {
        var storage = new FakePluginStorage();
        var settings = new KubernetesSettings(storage);

        settings.SetArgoToken("cluster-1", "argocd-token-value");
        settings.SetArgoToken("cluster-2", "token-for-2");

        Assert.Equal("argocd-token-value", settings.GetArgoToken("cluster-1"));
        Assert.Equal("token-for-2", settings.GetArgoToken("cluster-2"));
        Assert.Contains("cluster.cluster-1.argoToken", storage.SecretKeys);

        settings.ClearArgoToken("cluster-1");

        Assert.Null(settings.GetArgoToken("cluster-1"));
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
}
