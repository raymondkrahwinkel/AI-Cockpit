using System.Text.Json;
using k8s;
using Cockpit.Plugin.Kubernetes.Mcp;

namespace Cockpit.Plugin.Kubernetes.Tests;

/// <summary>
/// The schema-less shaping the tools lean on: the client deserializes any resource into <see cref="RawKubernetesObject"/>
/// via the k8s serializer, and the tool serializes it back out — this must not lose fields. And the list summary must
/// pull name/namespace out of each item. Both are exercised against literal payloads (the riskiest code the reviewers
/// flagged as untested).
/// </summary>
public class ResourceShapingTests
{
    [Fact]
    public void RawKubernetesObject_RoundTrips_KeepingAllFields()
    {
        const string json = """{"apiVersion":"v1","kind":"Pod","metadata":{"name":"nginx","namespace":"default"},"spec":{"replicas":3}}""";

        var resource = KubernetesJson.Deserialize<RawKubernetesObject>(json);
        Assert.Equal("v1", resource.ApiVersion);
        Assert.Equal("Pod", resource.Kind);
        Assert.Contains("metadata", resource.Data);
        Assert.Contains("spec", resource.Data);

        var node = JsonSerializer.SerializeToNode(resource);
        Assert.Equal("v1", node!["apiVersion"]!.GetValue<string>());
        Assert.Equal("Pod", node["kind"]!.GetValue<string>());
        Assert.Equal("nginx", node["metadata"]!["name"]!.GetValue<string>());
        Assert.Equal(3, node["spec"]!["replicas"]!.GetValue<int>());
    }

    [Fact]
    public void ResourceListSummary_ReturnsNameAndNamespacePerItem()
    {
        const string json = """
        {"apiVersion":"v1","kind":"PodList","items":[
          {"metadata":{"name":"a","namespace":"default","creationTimestamp":"2026-07-18T00:00:00Z"}},
          {"metadata":{"name":"b","namespace":"kube-system"}}
        ]}
        """;

        var list = KubernetesJson.Deserialize<RawKubernetesList>(json);
        var summary = ResourceListSummary.Summarize(list);

        Assert.Equal(2, summary["count"]!.GetValue<int>());
        Assert.Equal("a", summary["items"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("default", summary["items"]![0]!["namespace"]!.GetValue<string>());
        Assert.Equal("b", summary["items"]![1]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void ResourceListSummary_EmptyList_IsZeroItems()
    {
        var list = KubernetesJson.Deserialize<RawKubernetesList>("""{"apiVersion":"v1","kind":"PodList","items":[]}""");
        Assert.Equal(0, ResourceListSummary.Summarize(list)["count"]!.GetValue<int>());
    }
}
