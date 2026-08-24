using System.Text.Json.Nodes;

namespace Cockpit.Plugin.Kubernetes.Helm;

// AC-1061 fase 1, AC2: a resource Helm installed carries the `app.kubernetes.io/managed-by: Helm` label plus a pair
// of `meta.helm.sh/release-*` annotations, regardless of chart. Surfacing that here saves the caller from having to
// know the raw label/annotation names to tell a Helm-managed resource apart from one applied by hand.
internal static class HelmManagedDetector
{
    public const string PropertyName = "helmManaged";

    private const string ManagedByLabel = "app.kubernetes.io/managed-by";
    private const string ReleaseNameAnnotation = "meta.helm.sh/release-name";
    private const string ReleaseNamespaceAnnotation = "meta.helm.sh/release-namespace";

    public static JsonObject? Detect(JsonNode? resource)
    {
        var labels = resource?["metadata"]?["labels"] as JsonObject;
        if (_String(labels?[ManagedByLabel]) != "Helm")
        {
            return null;
        }

        var annotations = resource?["metadata"]?["annotations"] as JsonObject;
        return new JsonObject
        {
            ["releaseName"] = _String(annotations?[ReleaseNameAnnotation]),
            ["releaseNamespace"] = _String(annotations?[ReleaseNamespaceAnnotation]),
        };
    }

    private static string? _String(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
