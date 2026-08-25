using System.Text.Json.Nodes;
using Cockpit.Plugin.Kubernetes.Helm;

namespace Cockpit.Plugin.Kubernetes.Argo;

// AC-576 phase 2: an Argo CD-managed resource carries the app that owns it in the `tracking-id` annotation —
// `<app>:<group>/<kind>:<namespace>/<name>` (util/argo/resource_tracking.go:18), v3.3.2's default tracking
// method — or, when that annotation is absent, the `app.kubernetes.io/instance` label as a fallback.
internal static class ArgoManagedDetector
{
    public const string PropertyName = "argoManaged";

    private const string TrackingIdAnnotation = "argocd.argoproj.io/tracking-id";
    private const string InstanceLabel = "app.kubernetes.io/instance";

    public static JsonObject? Detect(JsonNode? resource)
    {
        var annotations = resource?["metadata"]?["annotations"] as JsonObject;
        if (_String(annotations?[TrackingIdAnnotation]) is { Length: > 0 } trackingId)
        {
            var application = trackingId.Split(':', 2)[0];
            return string.IsNullOrWhiteSpace(application) ? null : new JsonObject { ["application"] = application, ["source"] = "tracking-id" };
        }

        // `app.kubernetes.io/instance` is a generic recommended label, not an Argo one — most Helm charts set
        // it to the release name too. Reading it as Argo ownership on a genuine Helm release (both release
        // annotations present) would be AC-1068's exact mistake, the other way around.
        if (_String(annotations?[HelmManagedDetector.ReleaseNameAnnotation]) is not null
            && _String(annotations?[HelmManagedDetector.ReleaseNamespaceAnnotation]) is not null)
        {
            return null;
        }

        var labels = resource?["metadata"]?["labels"] as JsonObject;
        return _String(labels?[InstanceLabel]) is { Length: > 0 } instance
            ? new JsonObject { ["application"] = instance, ["source"] = "instance-label" }
            : null;
    }

    private static string? _String(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
