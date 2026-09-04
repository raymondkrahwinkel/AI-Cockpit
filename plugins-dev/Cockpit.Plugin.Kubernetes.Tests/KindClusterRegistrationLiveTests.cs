using k8s;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugin.Kubernetes.Cli;
using Cockpit.Plugin.Kubernetes.Cluster;
using Cockpit.Plugin.Kubernetes.Settings;

namespace Cockpit.Plugin.Kubernetes.Tests;

// AC-179 criterion 5, re-proven across the plugin split (AC-1083): a real kind cluster registered through the
// intent handler is immediately reachable by this plugin's own client, with no manual kubeconfig step. Drives the
// kind binary directly — a plugin test project cannot reference the Kind plugin. Off unless kind is on PATH.
public class KindClusterRegistrationLiveTests
{
    private const string ClusterName = "cockpit-ac1083-live";

    [Fact]
    public async Task AClusterRegisteredThroughTheIntent_IsImmediatelyReachable()
    {
        var cli = new CliRunner();
        var probe = await cli.RunAsync(new CliCommand("kind", ["--version"], new Dictionary<string, string>()), TimeSpan.FromSeconds(5));
        if (!probe.Succeeded)
        {
            return;
        }

        var settings = new KubernetesSettings(new FakePluginStorage());
        var intents = new ClusterRegistrationIntents(settings);
        var kubeconfigPath = Path.Combine(Directory.CreateTempSubdirectory("ac1083-live").FullName, $"{ClusterName}.kubeconfig");

        var created = await cli.RunAsync(
            new CliCommand("kind", ["create", "cluster", "--name", ClusterName, "--kubeconfig", kubeconfigPath], new Dictionary<string, string>()),
            TimeSpan.FromMinutes(10));
        Assert.True(created.Succeeded, created.Stderr);

        try
        {
            await intents.RegisterAsync(new PluginIntent("kind", "kubernetes", ClusterRegistrationIntents.RegisterAction, new Dictionary<string, string>
            {
                ["id"] = $"kind-{ClusterName}",
                ["label"] = ClusterName,
                ["context"] = $"kind-{ClusterName}",
                ["kubeconfigPath"] = kubeconfigPath,
            }));

            var registration = Assert.Single(settings.Clusters);
            using var connections = new ClusterConnectionFactory(settings);
            var (client, connectError) = connections.Connect(registration);
            Assert.Null(connectError);
            var namespaces = await client!.CoreV1.ListNamespaceAsync();
            Assert.Contains(namespaces.Items, @namespace => @namespace.Metadata.Name == "default");

            await intents.UnregisterAsync(new PluginIntent("kind", "kubernetes", ClusterRegistrationIntents.UnregisterAction, new Dictionary<string, string>
            {
                ["id"] = $"kind-{ClusterName}",
            }));
            Assert.Empty(settings.Clusters);
        }
        finally
        {
            await cli.RunAsync(
                new CliCommand("kind", ["delete", "cluster", "--name", ClusterName, "--kubeconfig", kubeconfigPath], new Dictionary<string, string>()),
                TimeSpan.FromMinutes(2));
        }
    }
}
