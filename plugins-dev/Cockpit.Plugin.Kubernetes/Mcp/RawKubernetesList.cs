using System.Text.Json.Serialization;
using k8s;

namespace Cockpit.Plugin.Kubernetes.Mcp;

/// <summary>
/// A schema-less Kubernetes list — the list counterpart of <see cref="RawKubernetesObject"/>, so the generic client
/// can deserialize any <c>*List</c> response and the tool can summarize its <see cref="Items"/> without a typed list
/// model per kind.
/// </summary>
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

/// <summary>
/// The slice of a list response's <c>metadata</c> that tells whether the server capped the page: a non-empty
/// <see cref="Continue"/> token (or a positive <see cref="RemainingItemCount"/>) means there is more beyond the limit.
/// </summary>
internal sealed class RawListMetadata
{
    [JsonPropertyName("continue")]
    public string? Continue { get; set; }

    [JsonPropertyName("remainingItemCount")]
    public long? RemainingItemCount { get; set; }
}
