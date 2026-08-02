using System.Text.Json.Serialization;
using k8s;

namespace Cockpit.Plugin.Kubernetes.Mcp;

// A schema-less Kubernetes list — the list counterpart of `RawKubernetesObject`, so the generic client
// can deserialize any `*List` response and the tool can summarize its `Items` without a typed list
// model per kind.
internal sealed class RawKubernetesList : IKubernetesObject
{
    [JsonPropertyName("apiVersion")]
    public string ApiVersion { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("metadata")]
    public RawListMetadata? Metadata { get; set; }

    [JsonPropertyName("items")]
    public List<RawKubernetesObject> Items { get; set; } = [];
}

// The slice of a list response's `metadata` that tells whether the server capped the page: a non-empty
// `Continue` token (or a positive `RemainingItemCount`) means there is more beyond the limit.
internal sealed class RawListMetadata
{
    [JsonPropertyName("continue")]
    public string? Continue { get; set; }

    [JsonPropertyName("remainingItemCount")]
    public long? RemainingItemCount { get; set; }
}
