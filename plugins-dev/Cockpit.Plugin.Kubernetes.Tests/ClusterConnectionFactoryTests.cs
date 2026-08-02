using Cockpit.Plugin.Kubernetes.Cluster;
using Cockpit.Plugin.Kubernetes.Model;
using Cockpit.Plugin.Kubernetes.Settings;

namespace Cockpit.Plugin.Kubernetes.Tests;

// The connection factory's error strings reach the agent through the tools, so they must name the cluster by its
// label and never leak the host's kubeconfig path (which names the user/home) — the point of the path staying out
// of `list_clusters` (security review, AC-83).
public class ClusterConnectionFactoryTests
{
    [Fact]
    public void Connect_MissingKubeconfigFile_ErrorNamesTheLabel_NotThePath()
    {
        var settings = new KubernetesSettings(new FakePluginStorage());
        const string path = "/home/somebody/.kube/secret-typo-path";
        var cluster = new ClusterRegistration("id-1", "prod", ContextName: "", ["default"], KubeconfigPath: path);
        settings.Clusters = [cluster];
        var factory = new ClusterConnectionFactory(settings);

        var (client, error) = factory.Connect(cluster);

        Assert.Null(client);
        Assert.NotNull(error);
        Assert.Contains("prod", error);
        Assert.DoesNotContain(path, error);
        Assert.DoesNotContain("somebody", error);
    }

    [Fact]
    public void Connect_NoPathAndNoSecret_ReportsMissingKubeconfig()
    {
        var settings = new KubernetesSettings(new FakePluginStorage());
        var cluster = new ClusterRegistration("id-1", "prod", ContextName: "", ["default"]);
        settings.Clusters = [cluster];
        var factory = new ClusterConnectionFactory(settings);

        var (client, error) = factory.Connect(cluster);

        Assert.Null(client);
        Assert.Contains("prod", error);
    }

    // H1: a port-forward tunnel must hold a client the cache never references, so a settings-save InvalidateAll
    // cannot dispose it out from under the live tunnel. ConnectDedicated builds exactly that.
    [Fact]
    public void ConnectDedicated_BuildsAFreshClientOutsideTheCache()
    {
        var settings = new KubernetesSettings(new FakePluginStorage());
        var cluster = new ClusterRegistration("id-1", "prod", ContextName: "", ["default"]);
        settings.SetKubeconfig(cluster.Id, MinimalKubeconfig);
        settings.Clusters = [cluster];
        var factory = new ClusterConnectionFactory(settings);

        var (cached1, _) = factory.Connect(cluster);
        var (cached2, _) = factory.Connect(cluster);
        var (dedicatedA, errorA) = factory.ConnectDedicated(cluster);
        var (dedicatedB, _) = factory.ConnectDedicated(cluster);

        Assert.Null(errorA);
        Assert.NotNull(cached1);
        Assert.Same(cached1, cached2);
        Assert.NotNull(dedicatedA);
        Assert.NotSame(cached1, dedicatedA);
        Assert.NotSame(dedicatedA, dedicatedB);

        dedicatedA!.Dispose();
        dedicatedB!.Dispose();
    }

    private const string MinimalKubeconfig = """
        apiVersion: v1
        kind: Config
        clusters:
        - name: c
          cluster:
            server: https://127.0.0.1:6443
        contexts:
        - name: ctx
          context:
            cluster: c
            user: u
        current-context: ctx
        users:
        - name: u
          user:
            token: test-token
        """;
}
