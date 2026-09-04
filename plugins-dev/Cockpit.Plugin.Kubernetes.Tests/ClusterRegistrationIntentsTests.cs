using Cockpit.Plugins.Abstractions;
using Cockpit.Plugin.Kubernetes.Cluster;
using Cockpit.Plugin.Kubernetes.Settings;

namespace Cockpit.Plugin.Kubernetes.Tests;

// The intent handlers another plugin registers a cluster through (AC-1083). One round trip rather than three
// tests: register, refuse to overwrite, unregister is the whole contract, and each step's state is the next
// step's input.
public class ClusterRegistrationIntentsTests
{
    [Fact]
    public async Task RegisterThenRegisterAgainThenUnregister()
    {
        var settings = new KubernetesSettings(new FakePluginStorage());
        var intents = new ClusterRegistrationIntents(settings);

        await intents.RegisterAsync(_Intent(ClusterRegistrationIntents.RegisterAction, new Dictionary<string, string>
        {
            ["id"] = "kind-demo",
            ["label"] = "demo",
            ["context"] = "kind-demo",
            ["kubeconfigPath"] = "/state/kind/demo.kubeconfig",
        }));

        var registration = Assert.Single(settings.Clusters);
        Assert.Equal("kind-demo", registration.Id);
        Assert.Equal("demo", registration.Label);
        Assert.Equal("kind-demo", registration.ContextName);
        Assert.Equal("/state/kind/demo.kubeconfig", registration.KubeconfigPath);
        Assert.Equal(["default"], registration.AllowedNamespaces);

        // Criterion 6: a registration with this id is never silently overwritten, and the caller is told why.
        var second = await intents.RegisterAsync(_Intent(ClusterRegistrationIntents.RegisterAction, new Dictionary<string, string>
        {
            ["id"] = "kind-demo",
            ["kubeconfigPath"] = "/somewhere/else.kubeconfig",
        }));

        Assert.Contains("already existed", second["notice"]);
        Assert.Equal("/state/kind/demo.kubeconfig", Assert.Single(settings.Clusters).KubeconfigPath);

        await intents.UnregisterAsync(_Intent(ClusterRegistrationIntents.UnregisterAction, new Dictionary<string, string> { ["id"] = "kind-demo" }));

        Assert.Empty(settings.Clusters);
    }

    private static PluginIntent _Intent(string action, IReadOnlyDictionary<string, string> data) =>
        new("kind", "kubernetes", action, data);
}
