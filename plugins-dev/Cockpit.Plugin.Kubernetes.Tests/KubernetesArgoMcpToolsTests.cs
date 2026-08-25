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

// AC-576 phase 1: proves the argo_* tools go through the plain namespaced read gate — the same one get_resource
// uses on an ordinary resource — not the Dangerous secret gate the Helm tools need. An Application is not
// credential material, so approving argo_apps must not ask a second, Dangerous prompt the way HelmList does.
public class KubernetesArgoMcpToolsTests
{
    private const string Session = "pane-1";
    private const string DummyKubeconfig = "apiVersion: v1\nkind: Config\nclusters: []\ncontexts: []\nusers: []\n";

    private static (KubernetesMcpTools Tools, List<ConsentRequest> Asked) _Build(ConsentOutcome outcome, bool withKubeconfig = true)
    {
        var cluster = new ClusterRegistration("id-1", "prod", ContextName: "", ["argocd"]);
        var storage = new FakePluginStorage();
        var settings = new KubernetesSettings(storage);
        settings.Clusters = [cluster];
        if (withKubeconfig)
        {
            settings.SetKubeconfig(cluster.Id, DummyKubeconfig);
        }

        var asked = new List<ConsentRequest>();
        var host = Substitute.For<ICockpitHost>();
        host.RequestConsentAsync(Arg.Do<ConsentRequest>(asked.Add)).Returns(new ConsentDecision(outcome));

        var gate = new ClusterAccessGate(host);
        var connections = new ClusterConnectionFactory(settings);
        return (new KubernetesMcpTools(settings, gate, connections, new PortForwardManager(), TestKindClusters.Unused(settings), host), asked);
    }

    [Fact]
    public async Task ArgoApps_UnknownCluster_IsACleanError()
    {
        var (tools, asked) = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.ArgoApps("does-not-exist", Session, "argocd"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("list_clusters", json["error"]!.GetValue<string>());
        Assert.Empty(asked);
    }

    [Fact]
    public async Task ArgoApps_WhenConsentDenied_StopsBeforeTheCluster()
    {
        var (tools, _) = _Build(ConsentOutcome.Denied);

        var json = JsonNode.Parse(await tools.ArgoApps("prod", Session, "argocd"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("did not approve", json["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task ArgoApps_WhenApproved_IsNotGatedAsDangerous_UnlikeHelm()
    {
        var (tools, asked) = _Build(ConsentOutcome.Approved, withKubeconfig: false);

        await tools.ArgoApps("prod", Session, "argocd");

        Assert.DoesNotContain(asked, request => request.Risk == ConsentRisk.Dangerous);
    }

    [Fact]
    public async Task ArgoApps_WhenApproved_ReachesTheConnection()
    {
        var (tools, _) = _Build(ConsentOutcome.Approved, withKubeconfig: false);

        var json = JsonNode.Parse(await tools.ArgoApps("prod", Session, "argocd"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("kubeconfig", json["error"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("ArgoApp")]
    [InlineData("ArgoHistory")]
    [InlineData("ArgoLastSync")]
    public async Task PerApplicationTools_UnknownCluster_IsACleanError(string toolMethod)
    {
        var (tools, asked) = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await _Invoke(tools, toolMethod, "does-not-exist"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("list_clusters", json["error"]!.GetValue<string>());
        Assert.Empty(asked);
    }

    [Theory]
    [InlineData("ArgoApp")]
    [InlineData("ArgoHistory")]
    [InlineData("ArgoLastSync")]
    public async Task PerApplicationTools_WhenConsentDenied_StopsBeforeTheCluster(string toolMethod)
    {
        var (tools, _) = _Build(ConsentOutcome.Denied);

        var json = JsonNode.Parse(await _Invoke(tools, toolMethod, "prod"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("did not approve", json["error"]!.GetValue<string>());
    }

    private static Task<string> _Invoke(KubernetesMcpTools tools, string toolMethod, string cluster) => toolMethod switch
    {
        "ArgoApp" => tools.ArgoApp(cluster, Session, "argocd", "cert-manager"),
        "ArgoHistory" => tools.ArgoHistory(cluster, Session, "argocd", "cert-manager"),
        "ArgoLastSync" => tools.ArgoLastSync(cluster, Session, "argocd", "cert-manager"),
        _ => throw new ArgumentOutOfRangeException(nameof(toolMethod)),
    };
}
