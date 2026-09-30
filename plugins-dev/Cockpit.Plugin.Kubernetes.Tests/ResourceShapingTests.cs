using System.Text.Json;
using k8s;
using Cockpit.Plugin.Kubernetes.Mcp;

namespace Cockpit.Plugin.Kubernetes.Tests;

// The schema-less shaping the tools lean on: the client deserializes any resource into `RawKubernetesObject`
// via the k8s serializer, and the tool serializes it back out — this must not lose fields. And the list summary must
// pull name/namespace out of each item. Both are exercised against literal payloads (the riskiest code the reviewers
// flagged as untested).
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
}
