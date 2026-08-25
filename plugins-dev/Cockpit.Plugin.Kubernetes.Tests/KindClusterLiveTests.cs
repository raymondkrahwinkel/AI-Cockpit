using k8s;
using Cockpit.Plugin.Kubernetes.Cli;
using Cockpit.Plugin.Kubernetes.Cluster;
using Cockpit.Plugin.Kubernetes.Kind;
using Cockpit.Plugin.Kubernetes.Settings;

namespace Cockpit.Plugin.Kubernetes.Tests;

// The half of kind_create no unit test can reach: what a real kind + container runtime produce, and whether the
// rest of the plugin can use the result with zero manual steps (AC-179 criteria 1, 5, 14). Off unless kind is on
// PATH — same skip shape as HelmUpgradeClusterTests: return, not a failed assertion.
public class KindClusterLiveTests
{
    private const string ClusterName = "cockpit-ac179-live";

    [Fact]
    public async Task KindCreate_AgainstARealKindAndDocker_IsImmediatelyUsableAndFullyCleansUp()
    {
        var cli = new CliRunner();
        var runtime = new KindRuntime(cli);
        if (!(await runtime.DetectAsync(CancellationToken.None)).IsInstalled)
        {
            return;
        }

        var settings = new KubernetesSettings(new FakePluginStorage());
        var directory = Directory.CreateTempSubdirectory("ac179-kind-live").FullName;
        var manager = new KindClusterManager(settings, cli, runtime, "kind", directory);

        var userKubeconfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kube", "config");
        var userKubeconfigBefore = File.Exists(userKubeconfigPath) ? File.ReadAllText(userKubeconfigPath) : null;

        var (record, createError) = await manager.CreateAsync(ClusterName, "live-test-owner", CancellationToken.None);
        Assert.Null(createError);
        Assert.NotNull(record);

        try
        {
            // Criterion 1: ~/.kube/config is left fully alone by a path-kubeconfig create.
            var userKubeconfigAfter = File.Exists(userKubeconfigPath) ? File.ReadAllText(userKubeconfigPath) : null;
            Assert.Equal(userKubeconfigBefore, userKubeconfigAfter);

            // Criterion 14: the exact guard HelmUpgradeClusterTests._Kubeconfig() uses — current-context must
            // start with "kind-" for COCKPIT_HELM_KIND_KUBECONFIG to accept this file.
            var context = File.ReadAllLines(record!.KubeconfigPath)
                .Where(line => line.StartsWith("current-context:", StringComparison.Ordinal))
                .Select(line => line["current-context:".Length..].Trim())
                .FirstOrDefault();
            Assert.StartsWith("kind-", context, StringComparison.Ordinal);

            // Criterion 5: reachable through the existing Kubernetes-plugin tools with no manual step.
            var registration = Assert.Single(settings.Clusters);
            Assert.Equal(record.KubeconfigPath, registration.KubeconfigPath);
            using var connections = new ClusterConnectionFactory(settings);
            var (client, connectError) = connections.Connect(registration);
            Assert.Null(connectError);
            var namespaces = await client!.CoreV1.ListNamespaceAsync();
            Assert.Contains(namespaces.Items, @namespace => @namespace.Metadata.Name == "default");
        }
        finally
        {
            var (deleted, deleteError) = await manager.DeleteAsync(ClusterName, CancellationToken.None);
            Assert.True(deleted, deleteError);
        }

        // Criterion 4, re-verified post-delete: no residue anywhere the plugin itself tracks.
        Assert.False(File.Exists(record!.KubeconfigPath));
        Assert.Empty(settings.KindClusters);
        Assert.Empty(settings.Clusters);
    }
}
