using System.Text.Json.Nodes;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugin.Kubernetes.Cluster;
using Cockpit.Plugin.Kubernetes.Mcp;
using Cockpit.Plugin.Kubernetes.Model;
using Cockpit.Plugin.Kubernetes.Security;
using Cockpit.Plugin.Kubernetes.Settings;
using FluentAssertions;
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
        return (new KubernetesMcpTools(settings, gate, connections), asked);
    }

    private static ClusterRegistration _Cluster(bool exec = false, bool clusterScoped = false) =>
        new("id-1", "prod", ContextName: "", ["default"], AllowClusterScoped: clusterScoped, AllowExec: exec);

    private static ConsentRequest? _WithScopePrefix(IEnumerable<ConsentRequest> asked, string prefix) =>
        asked.FirstOrDefault(request => request.Scope.StartsWith(prefix, StringComparison.Ordinal));

    [Fact]
    public void ListClusters_ShowsRegisteredClusters()
    {
        var (tools, _) = _Build(ConsentOutcome.Approved, _Cluster());

        var json = JsonNode.Parse(tools.ListClusters());

        json!["clusters"]!.AsArray().Should().ContainSingle();
        json["clusters"]![0]!["label"]!.GetValue<string>().Should().Be("prod");
    }

    [Fact]
    public async Task ListResources_UnknownCluster_IsACleanError()
    {
        var (tools, asked) = _Build(ConsentOutcome.Approved, _Cluster());

        var json = JsonNode.Parse(await tools.ListResources("does-not-exist", Session, "v1", "pods", "default"));

        json!["ok"]!.GetValue<bool>().Should().BeFalse();
        json["error"]!.GetValue<string>().Should().Contain("list_clusters");
        asked.Should().BeEmpty("an unknown cluster never reaches the consent gate");
    }

    [Fact]
    public async Task ListResources_WhenConsentDenied_StopsBeforeTheCluster()
    {
        var (tools, _) = _Build(ConsentOutcome.Denied, _Cluster());

        var json = JsonNode.Parse(await tools.ListResources("prod", Session, "v1", "pods", "kube-system"));

        json!["ok"]!.GetValue<bool>().Should().BeFalse();
        json["error"]!.GetValue<string>().Should().Contain("did not approve", "a denied consent is the error, not a cluster failure");
    }

    [Fact]
    public async Task Exec_WhenCapabilityOff_IsBlockedWithASettingsHint()
    {
        var (tools, asked) = _Build(ConsentOutcome.Approved, _Cluster(exec: false));

        var json = JsonNode.Parse(await tools.Exec("prod", Session, "default", "nginx", "ls"));

        json!["ok"]!.GetValue<bool>().Should().BeFalse();
        json["error"]!.GetValue<string>().Should().Contain("settings");
        asked.Should().BeEmpty("a capability that is off is a policy block — no prompt");
    }

    [Fact]
    public async Task ListResources_NamespacedKind_WithBlankNamespace_IsRefused_NotListedClusterWide()
    {
        // The F1 jail-escape: with cluster-scoped access ON, a namespaced kind (secrets) with a blank namespace must
        // NOT be treated as cluster-scoped and listed across every namespace — it must be refused for a namespace.
        var (tools, asked) = _Build(ConsentOutcome.Approved, _Cluster(clusterScoped: true));

        var json = JsonNode.Parse(await tools.ListResources("prod", Session, "v1", "secrets", @namespace: null));

        json!["ok"]!.GetValue<bool>().Should().BeFalse();
        json["error"]!.GetValue<string>().Should().Contain("namespace is required");
        asked.Should().BeEmpty("it is refused before any consent — never routed to the cluster-scoped gate");
    }

    [Fact]
    public async Task GetResource_Secret_AsksAfresh_EvenInsideAnAllowedNamespace()
    {
        // F2: reading a secret's contents in an allowed namespace still asks, as a Dangerous, never-remembered consent.
        var (tools, asked) = _Build(ConsentOutcome.Approved, _Cluster());

        await tools.GetResource("prod", Session, "v1", "secrets", "db-password", "default");

        var secretAsk = _WithScopePrefix(asked, "k8s.secret:");
        secretAsk.Should().NotBeNull("a secret is not \"free to read\" just because its namespace is allowed");
        secretAsk!.Risk.Should().Be(ConsentRisk.Dangerous);
        secretAsk.AllowRemember.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteResource_WhenApproved_ReachesTheConnection()
    {
        // Approved consent but no usable kubeconfig: the tool must get past the gate and fail at the connection,
        // proving the gate did not block an approved change.
        var (tools, _) = _Build(ConsentOutcome.Approved, _Cluster(), withKubeconfig: false);

        var json = JsonNode.Parse(await tools.DeleteResource("prod", Session, "v1", "pods", "nginx", "default"));

        json!["ok"]!.GetValue<bool>().Should().BeFalse();
        json["error"]!.GetValue<string>().Should().Contain("kubeconfig", "it passed the gate and stopped at the missing kubeconfig");
    }
}
