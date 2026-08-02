using System.Text.Json;
using System.Text.Json.Serialization;
using k8s;

namespace Cockpit.Plugin.Kubernetes.Mcp;

// A schema-less Kubernetes object: enough of `IKubernetesObject` for the generic client to (de)serialize
// any resource kind, with every other field captured by `Data`. This lets one code path read, patch or
// delete any resource without a typed model per kind — the plugin never needs to know a resource's shape, only its
// group/version/plural. Serializing it back yields the original JSON (the extension data flattens out).
internal sealed class RawKubernetesObject : IKubernetesObject
{
    [JsonPropertyName("apiVersion")]
    public string ApiVersion { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Data { get; set; } = [];
}
