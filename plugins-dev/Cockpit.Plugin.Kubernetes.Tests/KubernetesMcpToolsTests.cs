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

/// <summary>
/// The tools are the wiring between the agent and the gate — these prove that wiring holds: an unknown cluster is a
/// clean error, a denied consent stops before the cluster is ever reached, a capability that is off is a policy
/// block with a hint (no prompt), and an approved call does get as far as the connection. The cluster call itself
/// (against a live apiserver) is the operator's to verify.
/// </summary>
public class KubernetesMcpToolsTests
{
    private const string Session = "pane-1";
    private const string DummyKubeconfig = "apiVersion: v1\nkind: Config\nclusters: []\ncontexts: []\nusers: []\n";

    private static (KubernetesMcpTools Tools, List<ConsentRequest> Asked) _Build(ConsentOutcome outcome, ClusterRegistration cluster, bool withKubeconfig = true)
    {
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

    private static ClusterRegistration _Cluster(bool exec = false, bool clusterScoped = false, bool portForward = false) =>
        new("id-1", "prod", ContextName: "", ["default"], AllowClusterScoped: clusterScoped, AllowExec: exec, AllowPortForward: portForward);

    private static ConsentRequest? _WithScopePrefix(IEnumerable<ConsentRequest> asked, string prefix) =>
        asked.FirstOrDefault(request => request.Scope.StartsWith(prefix, StringComparison.Ordinal));

    [Fact]
    public void ListClusters_ShowsRegisteredClusters()
    {
        var (tools, _) = _Build(ConsentOutcome.Approved, _Cluster());

        var json = JsonNode.Parse(tools.ListClusters());

        Assert.Single(json!["clusters"]!.AsArray());
        Assert.Equal("prod", json["clusters"]![0]!["label"]!.GetValue<string>());
    }

    [Fact]
    public async Task ListResources_UnknownCluster_IsACleanError()
    {
        var (tools, asked) = _Build(ConsentOutcome.Approved, _Cluster());

        var json = JsonNode.Parse(await tools.ListResources("does-not-exist", Session, "v1", "pods", "default"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("list_clusters", json["error"]!.GetValue<string>());
        Assert.Empty(asked);
    }

    [Fact]
    public async Task ListResources_WhenConsentDenied_StopsBeforeTheCluster()
    {
        var (tools, _) = _Build(ConsentOutcome.Denied, _Cluster());

        var json = JsonNode.Parse(await tools.ListResources("prod", Session, "v1", "pods", "kube-system"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("did not approve", json["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Exec_WhenCapabilityOff_IsBlockedWithASettingsHint()
    {
        var (tools, asked) = _Build(ConsentOutcome.Approved, _Cluster(exec: false));

        var json = JsonNode.Parse(await tools.Exec("prod", Session, "default", "nginx", "ls"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("settings", json["error"]!.GetValue<string>());
        Assert.Empty(asked);
    }

    [Fact]
    public async Task ListResources_NamespacedKind_WithBlankNamespace_IsRefused_NotListedClusterWide()
    {
        // The F1 jail-escape: with cluster-scoped access ON, a namespaced kind (secrets) with a blank namespace must
        // NOT be treated as cluster-scoped and listed across every namespace — it must be refused for a namespace.
        var (tools, asked) = _Build(ConsentOutcome.Approved, _Cluster(clusterScoped: true));

        var json = JsonNode.Parse(await tools.ListResources("prod", Session, "v1", "secrets", @namespace: null));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("namespace is required", json["error"]!.GetValue<string>());
        Assert.Empty(asked);
    }

    [Fact]
    public async Task GetResource_Secret_AsksAfresh_EvenInsideAnAllowedNamespace()
    {
        // F2: reading a secret's contents in an allowed namespace still asks, as a Dangerous, never-remembered consent.
        var (tools, asked) = _Build(ConsentOutcome.Approved, _Cluster());

        await tools.GetResource("prod", Session, "v1", "secrets", "db-password", "default");

        var secretAsk = _WithScopePrefix(asked, "k8s.secret:");
        Assert.NotNull(secretAsk);
        Assert.Equal(ConsentRisk.Dangerous, secretAsk!.Risk);
        Assert.False(secretAsk.AllowRemember);
    }

    [Fact]
    public async Task PortForward_WhenCapabilityOff_IsBlockedWithASettingsHint()
    {
        var (tools, asked) = _Build(ConsentOutcome.Approved, _Cluster(portForward: false));

        var json = JsonNode.Parse(await tools.PortForward("prod", Session, "default", "nginx", 80));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("settings", json["error"]!.GetValue<string>());
        Assert.Empty(asked);
    }

    [Fact]
    public async Task DeleteResource_WhenApproved_ReachesTheConnection()
    {
        // Approved consent but no usable kubeconfig: the tool must get past the gate and fail at the connection,
        // proving the gate did not block an approved change.
        var (tools, _) = _Build(ConsentOutcome.Approved, _Cluster(), withKubeconfig: false);

        var json = JsonNode.Parse(await tools.DeleteResource("prod", Session, "v1", "pods", "nginx", "default"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("kubeconfig", json["error"]!.GetValue<string>());
    }
}
