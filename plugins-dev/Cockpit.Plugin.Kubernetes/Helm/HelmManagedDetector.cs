using System.Text.Json.Nodes;

namespace Cockpit.Plugin.Kubernetes.Helm;

// AC-1068: the `managed-by: Helm` label alone is not enough — Argo CD's `helm template` sets it too without
// installing anything, which used to report as installed with two empty strings. The release-name/-namespace
// annotations only exist when Helm itself did the install, so their presence is what separates the two.
internal static class HelmManagedDetector
{
    public const string PropertyName = "helmManaged";

    private const string ManagedByLabel = "app.kubernetes.io/managed-by";

    // Internal, not private: ArgoManagedDetector checks these too, to tell a genuine Helm release apart from
    // Argo's own `app.kubernetes.io/instance` fallback tracking label — the two share that label by convention.
    internal const string ReleaseNameAnnotation = "meta.helm.sh/release-name";
    internal const string ReleaseNamespaceAnnotation = "meta.helm.sh/release-namespace";

    private const string InstalledNote = "Read off this resource's own release annotations — call helm_history to confirm the release still exists.";
    private const string RenderedNote = "Labeled managed-by: Helm but carries no meta.helm.sh/release-* annotations — rendered without being installed (e.g. Argo CD's `helm template`), not a queryable Helm release.";

    public static JsonObject? Detect(JsonNode? resource)
    {
        var labels = resource?["metadata"]?["labels"] as JsonObject;
        if (_String(labels?[ManagedByLabel]) != "Helm")
        {
            return null;
        }

        var annotations = resource?["metadata"]?["annotations"] as JsonObject;
        var releaseName = _String(annotations?[ReleaseNameAnnotation]);
        var releaseNamespace = _String(annotations?[ReleaseNamespaceAnnotation]);
        var installed = releaseName is not null && releaseNamespace is not null;

        return new JsonObject
        {
            ["installed"] = installed,
            ["releaseName"] = releaseName,
            ["releaseNamespace"] = releaseNamespace,
            ["note"] = installed ? InstalledNote : RenderedNote,
        };
    }

    private static string? _String(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
