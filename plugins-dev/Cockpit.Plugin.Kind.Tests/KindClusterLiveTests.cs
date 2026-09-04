using Cockpit.Plugin.Kind.Cli;
using Cockpit.Plugin.Kind.Settings;

namespace Cockpit.Plugin.Kind.Tests;

// The half of kind_create no unit test can reach: what a real kind + container runtime produce (AC-179 criteria 1,
// 4, 14). Off unless kind is on PATH — return, not a failed assertion.
public class KindClusterLiveTests
{
    private const string ClusterName = "cockpit-ac179-live";

    [Fact]
    public async Task KindCreate_AgainstARealKindAndDocker_WritesItsOwnKubeconfigAndFullyCleansUp()
    {
        var cli = new CliRunner();
        var runtime = new KindRuntime(cli);
        if (!(await runtime.DetectAsync(CancellationToken.None)).IsInstalled)
        {
            return;
        }

        var settings = new KindSettings(new FakePluginStorage());
        var directory = Directory.CreateTempSubdirectory("ac179-kind-live").FullName;
        var manager = new KindClusterManager(settings, cli, runtime, "kind", directory);

        var userKubeconfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kube", "config");
        var userKubeconfigBefore = File.Exists(userKubeconfigPath) ? File.ReadAllText(userKubeconfigPath) : null;

        // No host, so nothing registers with the Kubernetes plugin — that half is proven on its side
        // (KindClusterRegistrationLiveTests). Here the notice is expected, not a failure.
        var (record, notice) = await manager.CreateAsync(ClusterName, "live-test-owner", CancellationToken.None);
        Assert.NotNull(record);
        Assert.Contains("not registered", notice);

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
        }
        finally
        {
            var (deleted, deleteError) = await manager.DeleteAsync(ClusterName, CancellationToken.None);
            Assert.True(deleted, deleteError);
        }

        // Criterion 4, re-verified post-delete: no residue anywhere the plugin itself tracks.
        Assert.False(File.Exists(record!.KubeconfigPath));
        Assert.Empty(settings.KindClusters);
    }
}
