using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugin.Kubernetes.Cluster;
using Cockpit.Plugin.Kubernetes.Helm;
using Cockpit.Plugin.Kubernetes.Mcp;
using Cockpit.Plugin.Kubernetes.Model;
using Cockpit.Plugin.Kubernetes.Security;
using Cockpit.Plugin.Kubernetes.Settings;
using NSubstitute;
using k8s.Models;

namespace Cockpit.Plugin.Kubernetes.Tests;

// AC-1061 fase 2: helm_rollback is the plugin's first tool that changes a whole release at once. These pin the two
// halves that must not drift — the gate (a change asks afresh, and a refusal stops before the cluster) and the
// release-secret bookkeeping helm reads back afterwards.
public class HelmRollbackTests
{
    private const string Session = "pane-1";
    private const string DummyKubeconfig = "apiVersion: v1\nkind: Config\nclusters: []\ncontexts: []\nusers: []\n";

    private static readonly JsonObject TargetRelease = (JsonObject)JsonNode.Parse("""
        {
          "name": "traefik",
          "namespace": "system-ingress",
          "version": 6,
          "info": { "status": "superseded", "first_deployed": "2025-01-10T09:00:00Z", "last_deployed": "2025-05-02T10:00:00Z" },
          "chart": { "metadata": { "name": "traefik", "version": "35.3.0", "appVersion": "v3.4.0" } },
          "config": { "replicas": 1 },
          "manifest": "apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: traefik\n"
        }
        """)!;

    private static (KubernetesMcpTools Tools, List<ConsentRequest> Asked) _Build(ConsentOutcome outcome)
    {
        var cluster = new ClusterRegistration("id-1", "prod", ContextName: "", ["system-ingress"]);
        var settings = new KubernetesSettings(new FakePluginStorage());
        settings.Clusters = [cluster];
        settings.SetKubeconfig(cluster.Id, DummyKubeconfig);

        var asked = new List<ConsentRequest>();
        var host = Substitute.For<ICockpitHost>();
        host.RequestConsentAsync(Arg.Do<ConsentRequest>(asked.Add)).Returns(new ConsentDecision(outcome));

        var connections = new ClusterConnectionFactory(settings);
        return (new KubernetesMcpTools(settings, new ClusterAccessGate(host), connections, new PortForwardManager()), asked);
    }

    [Fact]
    public async Task HelmRollback_UnknownCluster_IsACleanError()
    {
        var (tools, asked) = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.HelmRollback("does-not-exist", Session, "system-ingress", "traefik", 6));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Empty(asked);
    }

    [Fact]
    public async Task HelmRollback_ARevisionBelowOne_IsRefusedBeforeAnythingIsAsked()
    {
        var (tools, asked) = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.HelmRollback("prod", Session, "system-ingress", "traefik", 0));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("helm_history", json["error"]!.GetValue<string>());
        Assert.Empty(asked);
    }

    [Fact]
    public async Task HelmRollback_WhenConsentDenied_StopsBeforeTheCluster()
    {
        var (tools, _) = _Build(ConsentOutcome.Denied);

        var json = JsonNode.Parse(await tools.HelmRollback("prod", Session, "system-ingress", "traefik", 6));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("did not approve", json["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task HelmRollback_ReadingTheRevisions_IsGatedAsCredentialMaterial()
    {
        var (tools, asked) = _Build(ConsentOutcome.Approved);

        await tools.HelmRollback("prod", Session, "system-ingress", "traefik", 6);

        var request = Assert.Single(asked, candidate => candidate.Scope.StartsWith("k8s.secret:", StringComparison.Ordinal));
        Assert.Equal(ConsentRisk.Dangerous, request.Risk);
        Assert.False(request.AllowRemember);
    }

    [Fact]
    public void NewRevision_WritesAFreshRevisionRatherThanResurrectingTheOldOne()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_770_000_000);

        var (secret, payload) = HelmReleaseLedger.NewRevision(TargetRelease, "traefik", "system-ingress", 8, 6, now);

        Assert.Equal("sh.helm.release.v1.traefik.v8", secret.Metadata.Name);
        Assert.Equal(HelmReleaseLedger.SecretType, secret.Type);
        Assert.Equal("8", secret.Metadata.Labels["version"]);
        Assert.Equal("helm", secret.Metadata.Labels["owner"]);
        Assert.Equal(HelmReleaseLedger.PendingRollback, secret.Metadata.Labels["status"]);
        Assert.Equal("1770000000", secret.Metadata.Labels["modifiedAt"]);

        // The payload carries the target's manifest under the new number, and says so the way helm does.
        Assert.Equal(8, payload["version"]!.GetValue<int>());
        Assert.Equal("Rollback to 6", payload["info"]!["description"]!.GetValue<string>());
        Assert.Equal(HelmReleaseLedger.PendingRollback, payload["info"]!["status"]!.GetValue<string>());
        Assert.Equal("2025-01-10T09:00:00Z", payload["info"]!["first_deployed"]!.GetValue<string>());
        Assert.Equal(6, TargetRelease["version"]!.GetValue<int>());
    }

    [Fact]
    public void Restamp_MovesTheStatusInThePayloadAndTheLabelTogether()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_770_000_000);
        var (secret, payload) = HelmReleaseLedger.NewRevision(TargetRelease, "traefik", "system-ingress", 8, 6, now);

        HelmReleaseLedger.Restamp(secret, payload, HelmReleaseLedger.Deployed, now.AddMinutes(1));

        Assert.Equal(HelmReleaseLedger.Deployed, secret.Metadata.Labels["status"]);
        Assert.Equal("1770000060", secret.Metadata.Labels["modifiedAt"]);
        var written = HelmReleaseSecretCodec.TryDecodeRaw(secret, out var error);
        Assert.Null(error);
        Assert.Equal(HelmReleaseLedger.Deployed, written!["info"]!["status"]!.GetValue<string>());
        Assert.Equal(8, written["version"]!.GetValue<int>());
    }

    [Fact]
    public void Encode_WritesTimestampOffsetsLiterally_BecauseHelmParsesThemFromTheRawBytes()
    {
        var withOffset = (JsonObject)TargetRelease.DeepClone();
        withOffset["info"]!["first_deployed"] = "2026-08-24T15:35:43.887449942+02:00";

        var (secret, _) = HelmReleaseLedger.NewRevision(withOffset, "traefik", "system-ingress", 8, 6, DateTimeOffset.UnixEpoch);

        // Helm's own time type parses the JSON string's raw bytes without undoing escapes, so a "+" written as
        // "\u002B" — what System.Text.Json does by default — leaves a revision helm silently drops from its history.
        // A round-trip through this codec cannot catch that, because our decoder unescapes correctly.
        Assert.Contains("+02:00", _RawJson(secret));
        Assert.DoesNotContain("u002B", _RawJson(secret));
    }

    private static string _RawJson(V1Secret secret)
    {
        var gzip = Convert.FromBase64String(Encoding.ASCII.GetString(secret.Data["release"]));
        using var stream = new GZipStream(new MemoryStream(gzip), CompressionMode.Decompress);
        return new StreamReader(stream, Encoding.UTF8).ReadToEnd();
    }

    [Fact]
    public void Encode_RoundTripsThroughTheSameUnpackHelmWrote()
    {
        var (secret, _) = HelmReleaseLedger.NewRevision(TargetRelease, "traefik", "system-ingress", 8, 6, DateTimeOffset.UnixEpoch);

        var release = HelmReleaseSecretCodec.TryDecode(secret, out var error);

        Assert.Null(error);
        Assert.Equal("traefik", release!.Name);
        Assert.Equal(8, release.Revision);
        Assert.Equal("traefik", release.ChartName);
        Assert.Equal("v3.4.0", release.AppVersion);
    }
}
