using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Cockpit.Plugin.Kubernetes.Helm;
using k8s.Models;

namespace Cockpit.Plugin.Kubernetes.Tests;

// AC-1061 fase 1: the one place the double-base64 + gzip + JSON unpack happens, and exactly the logic that can go
// stealthily wrong. Pins the decode against a release shaped like the verified cert-manager secret on
// "EVE Workbench - Production", and that a malformed secret fails cleanly instead of throwing or reading garbage.
public class HelmReleaseSecretCodecTests
{
    private const string ReleaseJson = """
        {
          "name": "cert-manager",
          "namespace": "system-ingress",
          "version": 1,
          "info": {
            "status": "deployed",
            "first_deployed": "2025-01-10T09:00:00Z",
            "last_deployed": "2025-01-10T09:00:00Z",
            "notes": "cert-manager has been deployed."
          },
          "chart": {
            "metadata": { "name": "cert-manager", "version": "1.4.16", "appVersion": "1.17.2" },
            "values": { "installCRDs": false }
          },
          "config": { "installCRDs": true, "replicaCount": 2 },
          "manifest": "apiVersion: apps/v1\nkind: Deployment\n"
        }
        """;

    [Fact]
    public void TryDecode_WellFormedReleaseSecret_ExtractsAllFields()
    {
        var secret = _Secret("sh.helm.release.v1.cert-manager.v1", ReleaseJson);

        var release = HelmReleaseSecretCodec.TryDecode(secret, out var error);

        Assert.Null(error);
        Assert.NotNull(release);
        Assert.Equal("cert-manager", release!.Name);
        Assert.Equal("system-ingress", release.Namespace);
        Assert.Equal(1, release.Revision);
        Assert.Equal("deployed", release.Status);
        Assert.Equal("cert-manager", release.ChartName);
        Assert.Equal("1.4.16", release.ChartVersion);
        Assert.Equal("1.17.2", release.AppVersion);
        Assert.Equal("cert-manager has been deployed.", release.Notes);
        Assert.Contains("kind: Deployment", release.Manifest);
        Assert.True(release.Config!["installCRDs"]!.GetValue<bool>());
        Assert.False(release.ChartDefaultValues!["installCRDs"]!.GetValue<bool>());
    }

    [Fact]
    public void TryDecode_ReleaseEntry_ProjectsIntoListStatusHistoryValuesManifestShapes()
    {
        var release = HelmReleaseSecretCodec.TryDecode(_Secret("sh.helm.release.v1.cert-manager.v1", ReleaseJson), out _)!;

        Assert.Equal("cert-manager-1.4.16", release.ToListEntry()["chart"]!.GetValue<string>());
        Assert.Equal("deployed", release.ToHistoryEntry()["status"]!.GetValue<string>());
        Assert.Equal("1.17.2", release.ToStatus()["appVersion"]!.GetValue<string>());
        Assert.False(release.ToValues(includeChartDefaults: false).ContainsKey("chartDefaultValues"));
        Assert.True(release.ToValues(includeChartDefaults: true).ContainsKey("chartDefaultValues"));
        Assert.Contains("Deployment", release.ToManifest()["manifest"]!.GetValue<string>());
    }

    [Fact]
    public void TryDecode_SecretWithoutReleaseKey_FailsCleanly_NotAnException()
    {
        var secret = new V1Secret { Metadata = new V1ObjectMeta { Name = "not-helm" }, Data = new Dictionary<string, byte[]> { ["other"] = "x"u8.ToArray() } };

        var release = HelmReleaseSecretCodec.TryDecode(secret, out var error);

        Assert.Null(release);
        Assert.Contains("not-helm", error);
        Assert.Contains("release", error);
    }

    // Each layer of the encoding, broken in turn: the base64 Helm wrote, then the gzip inside it. Neither may
    // throw, and neither may read garbage back as a release — the secret's own name has to reach the error so an
    // operator knows which release could not be read.
    public static IEnumerable<object[]> MalformedReleasePayloads() =>
    [
        ["not valid base64!!"u8.ToArray()],
        [Encoding.ASCII.GetBytes(Convert.ToBase64String("hello"u8.ToArray()))],
    ];

    [Theory]
    [MemberData(nameof(MalformedReleasePayloads))]
    public void TryDecode_AMalformedReleasePayload_FailsCleanly_NotAnException(byte[] payload)
    {
        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta { Name = "sh.helm.release.v1.broken.v1" },
            Data = new Dictionary<string, byte[]> { ["release"] = payload },
        };

        var release = HelmReleaseSecretCodec.TryDecode(secret, out var error);

        Assert.Null(release);
        Assert.Contains("broken", error);
    }

    // AC-1061 phase 5, §2c: `helm upgrade --output json` prints the same `release.Release` struct as JSON directly
    // (no gzip/base64 — that layer is only in the secret), so the typed record this plugin already has for reads
    // is also the typed record for the CLI's JSON output; no separate model is needed for it.
    [Fact]
    public void FromJson_ParsesTheShapeHelmCliPrintsForOutputJson_WithoutTheSecretEncoding()
    {
        var release = HelmRelease.FromJson((JsonObject)JsonNode.Parse(ReleaseJson)!);

        Assert.Equal("cert-manager", release.Name);
        Assert.Equal(1, release.Revision);
        Assert.Equal("deployed", release.Status);
        Assert.Equal("1.4.16", release.ChartVersion);
    }

    // Mirrors exactly how Helm writes the secret: the release JSON is gzipped, base64-encoded (Helm's own layer),
    // then handed to Kubernetes as the secret's Data — which `V1Secret.Data` already base64-decodes for us, so the
    // bytes here are the ASCII text of Helm's base64 layer, same as `KubernetesClient` would hand the plugin.
    private static V1Secret _Secret(string name, string releaseJson)
    {
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionMode.Compress, leaveOpen: true))
        {
            var jsonBytes = Encoding.UTF8.GetBytes(releaseJson);
            gzip.Write(jsonBytes, 0, jsonBytes.Length);
        }

        var helmLayerBase64 = Convert.ToBase64String(compressed.ToArray());
        return new V1Secret
        {
            Metadata = new V1ObjectMeta { Name = name },
            Data = new Dictionary<string, byte[]> { ["release"] = Encoding.ASCII.GetBytes(helmLayerBase64) },
        };
    }
}
