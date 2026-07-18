using System.Text.Json;
using k8s;
using Cockpit.Plugin.Kubernetes.Mcp;
using FluentAssertions;

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
        resource.ApiVersion.Should().Be("v1");
        resource.Kind.Should().Be("Pod");
        resource.Data.Should().ContainKey("metadata").And.ContainKey("spec");

        var node = JsonSerializer.SerializeToNode(resource);
        node!["apiVersion"]!.GetValue<string>().Should().Be("v1");
        node["kind"]!.GetValue<string>().Should().Be("Pod");
        node["metadata"]!["name"]!.GetValue<string>().Should().Be("nginx");
        node["spec"]!["replicas"]!.GetValue<int>().Should().Be(3);
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

        summary["count"]!.GetValue<int>().Should().Be(2);
        summary["items"]![0]!["name"]!.GetValue<string>().Should().Be("a");
        summary["items"]![0]!["namespace"]!.GetValue<string>().Should().Be("default");
        summary["items"]![1]!["name"]!.GetValue<string>().Should().Be("b");
    }

    [Fact]
    public void ResourceListSummary_EmptyList_IsZeroItems()
    {
        var list = KubernetesJson.Deserialize<RawKubernetesList>("""{"apiVersion":"v1","kind":"PodList","items":[]}""");
        ResourceListSummary.Summarize(list)["count"]!.GetValue<int>().Should().Be(0);
    }
}
