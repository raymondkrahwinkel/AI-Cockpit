using Cockpit.Plugins.Abstractions;
using Cockpit.Plugin.Kubernetes.Cluster;
using Cockpit.Plugin.Kubernetes.Model;
using Cockpit.Plugin.Kubernetes.Settings;

namespace Cockpit.Plugin.Kubernetes.Tests;

// The intent handlers another plugin registers a cluster through (AC-1083). One round trip rather than three
// tests: register, refuse to overwrite, unregister is the whole contract, and each step's state is the next
// step's input.
public class ClusterRegistrationIntentsTests
{
    // AC-1349 review M1: only the Kind plugin may name a consent mode; the same request from any other caller
    // registers as AlwaysAsk. The "kind" row keeps the other one from passing on a mode that is never honoured.
    [Theory]
    [InlineData("kind", ClusterConsentMode.ReadFree)]
    [InlineData("some-other-plugin", ClusterConsentMode.AlwaysAsk)]
    public async Task Register_HonoursAConsentModeOnlyFromTheKindPlugin(string caller, ClusterConsentMode expected)
    {
        var settings = new KubernetesSettings(new FakePluginStorage());
        var intents = new ClusterRegistrationIntents(settings);

        await intents.RegisterAsync(new PluginIntent(caller, "kubernetes", ClusterRegistrationIntents.RegisterAction, new Dictionary<string, string>
        {
            ["id"] = "kind-demo",
            ["context"] = "kind-demo",
            ["kubeconfigPath"] = "/state/kind/demo.kubeconfig",
            ["consentMode"] = "ReadFree",
        }));

        Assert.Equal(expected, Assert.Single(settings.Clusters).EffectiveConsentMode());
    }
}
