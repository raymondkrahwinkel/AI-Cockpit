using System.Text.Json.Nodes;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugin.Kubernetes.Cluster;
using Cockpit.Plugin.Kubernetes.Mcp;
using Cockpit.Plugin.Kubernetes.Model;
using Cockpit.Plugin.Kubernetes.Security;
using Cockpit.Plugin.Kubernetes.Settings;
using NSubstitute;

namespace Cockpit.Plugin.Kubernetes.Tests;

// AC-576 phase 5, AC 5: argo_sync refuses outright, before touching the cluster, when there is no Argo token.
// The gate/diff/patch machinery beyond that needs a real Argo API and apiserver — pinned directly instead in
// ArgoManagedResourcesDiffTests and ArgoSyncOperationTests; no dev cluster was available for the full call.
public class KubernetesArgoSyncMcpToolsTests
{
    private const string Session = "pane-1";
    private const string DummyKubeconfig = "apiVersion: v1\nkind: Config\nclusters: []\ncontexts: []\nusers: []\n";

    private static (KubernetesMcpTools Tools, List<ConsentRequest> Asked, KubernetesSettings Settings, ClusterRegistration Cluster) _Build(
        ConsentOutcome outcome, bool withKubeconfig = true, bool withArgoToken = false)
    {
        var cluster = new ClusterRegistration("id-1", "prod", ContextName: "", ["argocd"]);
        var storage = new FakePluginStorage();
        var settings = new KubernetesSettings(storage);
        settings.Clusters = [cluster];
        if (withKubeconfig)
        {
            settings.SetKubeconfig(cluster.Id, DummyKubeconfig);
        }

        if (withArgoToken)
        {
            settings.SetArgoToken(cluster.Id, "argocd-token-value");
        }

        var asked = new List<ConsentRequest>();
        var host = Substitute.For<ICockpitHost>();
        host.RequestConsentAsync(Arg.Do<ConsentRequest>(asked.Add)).Returns(new ConsentDecision(outcome));

        var gate = new ClusterAccessGate(host);
        var connections = new ClusterConnectionFactory(settings);
        return (new KubernetesMcpTools(settings, gate, connections, new PortForwardManager(), TestKindClusters.Unused(settings), host), asked, settings, cluster);
    }

    [Fact]
    public async Task ArgoSync_UnknownCluster_IsACleanError()
    {
        var (tools, asked, _, _) = _Build(ConsentOutcome.Approved, withArgoToken: true);

        var json = JsonNode.Parse(await tools.ArgoSync("does-not-exist", Session, "argocd", "cert-manager"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("list_clusters", json["error"]!.GetValue<string>());
        Assert.Empty(asked);
    }

    // AC-576 acceptance criterion 5, literally: no token, no diff, no approval asked, nothing written.
    [Fact]
    public async Task ArgoSync_NoArgoToken_RefusesBeforeAskingForApproval_AndBeforeReachingTheCluster()
    {
        var (tools, asked, _, _) = _Build(ConsentOutcome.Approved, withKubeconfig: false, withArgoToken: false);

        var json = JsonNode.Parse(await tools.ArgoSync("prod", Session, "argocd", "cert-manager"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("No Argo CD API token", json["error"]!.GetValue<string>());
        Assert.Empty(asked);
    }

    [Fact]
    public async Task ArgoSync_WithToken_ReachesTheConnection()
    {
        var (tools, _, _, _) = _Build(ConsentOutcome.Approved, withKubeconfig: false, withArgoToken: true);

        var json = JsonNode.Parse(await tools.ArgoSync("prod", Session, "argocd", "cert-manager"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("kubeconfig", json["error"]!.GetValue<string>());
    }
}
