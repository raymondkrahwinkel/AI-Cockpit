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

// AC-576 phase 4, AC 7: argo_refresh is the first Argo tool that writes anything. These prove the card is
// gated on its own scope (not the generic resource-mutation bucket) and, per the ticket's own acceptance
// criterion, that its literal text says nothing changes — not just that the call succeeds.
public class KubernetesArgoRefreshMcpToolsTests
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
        return (new KubernetesMcpTools(settings, gate, connections, new PortForwardManager()), asked);
    }

    [Fact]
    public async Task ArgoRefresh_UnknownCluster_IsACleanError()
    {
        var (tools, asked) = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.ArgoRefresh("does-not-exist", Session, "argocd", "cert-manager"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("list_clusters", json["error"]!.GetValue<string>());
        Assert.Empty(asked);
    }

    [Fact]
    public async Task ArgoRefresh_WhenConsentDenied_StopsBeforeTheCluster()
    {
        var (tools, _) = _Build(ConsentOutcome.Denied);

        var json = JsonNode.Parse(await tools.ArgoRefresh("prod", Session, "argocd", "cert-manager"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("did not approve", json["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task ArgoRefresh_ConsentCard_UsesItsOwnScope_NotTheGenericMutationBucket()
    {
        var (tools, asked) = _Build(ConsentOutcome.Approved, withKubeconfig: false);

        await tools.ArgoRefresh("prod", Session, "argocd", "cert-manager");

        var refreshAsk = asked.FirstOrDefault(request => request.Scope.StartsWith("k8s.argo.refresh:", StringComparison.Ordinal));
        Assert.NotNull(refreshAsk);
        Assert.Equal(ConsentRisk.Dangerous, refreshAsk!.Risk);
        Assert.False(refreshAsk.AllowRemember);
        Assert.DoesNotContain(asked, request => request.Scope.StartsWith("k8s.mutate:", StringComparison.Ordinal));
    }

    // AC-576 acceptance criterion 7, literally: the card must say a refresh changes nothing, not just be gated
    // as if it were a real change.
    [Fact]
    public async Task ArgoRefresh_ConsentCard_LiterallySaysNothingChanges()
    {
        var (tools, asked) = _Build(ConsentOutcome.Approved, withKubeconfig: false);

        await tools.ArgoRefresh("prod", Session, "argocd", "cert-manager");

        var refreshAsk = asked.First(request => request.Scope.StartsWith("k8s.argo.refresh:", StringComparison.Ordinal));
        Assert.Contains("nothing is deployed, updated or deleted", refreshAsk.Action);
    }

    [Fact]
    public async Task ArgoRefresh_Hard_ReflectsHardInTheCard()
    {
        var (tools, asked) = _Build(ConsentOutcome.Approved, withKubeconfig: false);

        await tools.ArgoRefresh("prod", Session, "argocd", "cert-manager", hard: true);

        var refreshAsk = asked.First(request => request.Scope.StartsWith("k8s.argo.refresh:", StringComparison.Ordinal));
        Assert.Contains("(hard)", refreshAsk.Action);
    }

    [Fact]
    public async Task ArgoRefresh_WhenApproved_ReachesTheConnection()
    {
        var (tools, _) = _Build(ConsentOutcome.Approved, withKubeconfig: false);

        var json = JsonNode.Parse(await tools.ArgoRefresh("prod", Session, "argocd", "cert-manager"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("kubeconfig", json["error"]!.GetValue<string>());
    }
}
