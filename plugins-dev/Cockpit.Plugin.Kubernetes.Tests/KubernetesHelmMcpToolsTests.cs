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

// AC-1061 fase 1: the Helm read tools go through the same gate as get_resource on a secret — a release secret IS
// credential material. These prove the wiring: an unknown cluster is a clean error, a denied consent stops before
// the cluster is reached, and an approved call is gated Dangerous/never remembered (like GetResource on a secret).
public class KubernetesHelmMcpToolsTests
{
    private const string Session = "pane-1";
    private const string DummyKubeconfig = "apiVersion: v1\nkind: Config\nclusters: []\ncontexts: []\nusers: []\n";

    private static (KubernetesMcpTools Tools, List<ConsentRequest> Asked) _Build(ConsentOutcome outcome, bool withKubeconfig = true)
    {
        var cluster = new ClusterRegistration("id-1", "prod", ContextName: "", ["default"]);
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
    public async Task HelmList_UnknownCluster_IsACleanError()
    {
        var (tools, asked) = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.HelmList("does-not-exist", Session, "default"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("list_clusters", json["error"]!.GetValue<string>());
        Assert.Empty(asked);
    }

    [Fact]
    public async Task HelmList_WhenConsentDenied_StopsBeforeTheCluster()
    {
        var (tools, _) = _Build(ConsentOutcome.Denied);

        var json = JsonNode.Parse(await tools.HelmList("prod", Session, "default"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("did not approve", json["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task HelmList_WhenApproved_IsGatedAsDangerousAndNeverRemembered()
    {
        var (tools, asked) = _Build(ConsentOutcome.Approved, withKubeconfig: false);

        await tools.HelmList("prod", Session, "default");

        // Two prompts are expected here: opening the connection (LowRisk, remembered) and then the secret read
        // itself (Dangerous, never remembered) — mirrors GetResource_Secret_AsksAfresh_EvenInsideAnAllowedNamespace.
        var request = asked.FirstOrDefault(candidate => candidate.Scope.StartsWith("k8s.secret:", StringComparison.Ordinal));
        Assert.NotNull(request);
        Assert.Equal(ConsentRisk.Dangerous, request!.Risk);
        Assert.False(request.AllowRemember);
    }

    [Fact]
    public async Task HelmList_WhenApproved_ReachesTheConnection()
    {
        var (tools, _) = _Build(ConsentOutcome.Approved, withKubeconfig: false);

        var json = JsonNode.Parse(await tools.HelmList("prod", Session, "default"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("kubeconfig", json["error"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("HelmStatus")]
    [InlineData("HelmHistory")]
    [InlineData("HelmValues")]
    [InlineData("HelmManifest")]
    public async Task SingleReleaseTools_UnknownCluster_IsACleanError(string toolMethod)
    {
        var (tools, asked) = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await _Invoke(tools, toolMethod, "does-not-exist"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("list_clusters", json["error"]!.GetValue<string>());
        Assert.Empty(asked);
    }

    [Theory]
    [InlineData("HelmStatus")]
    [InlineData("HelmHistory")]
    [InlineData("HelmValues")]
    [InlineData("HelmManifest")]
    public async Task SingleReleaseTools_WhenConsentDenied_StopsBeforeTheCluster(string toolMethod)
    {
        var (tools, _) = _Build(ConsentOutcome.Denied);

        var json = JsonNode.Parse(await _Invoke(tools, toolMethod, "prod"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("did not approve", json["error"]!.GetValue<string>());
    }

    private static Task<string> _Invoke(KubernetesMcpTools tools, string toolMethod, string cluster) => toolMethod switch
    {
        "HelmStatus" => tools.HelmStatus(cluster, Session, "default", "cert-manager"),
        "HelmHistory" => tools.HelmHistory(cluster, Session, "default", "cert-manager"),
        "HelmValues" => tools.HelmValues(cluster, Session, "default", "cert-manager"),
        "HelmManifest" => tools.HelmManifest(cluster, Session, "default", "cert-manager"),
        _ => throw new ArgumentOutOfRangeException(nameof(toolMethod)),
    };
}
