using System.Text.Json.Nodes;
using Cockpit.Plugin.Kubernetes.Argo;
using Cockpit.Plugin.Kubernetes.Helm;

namespace Cockpit.Plugin.Kubernetes.Mcp;

// AC-576 phase 2: composes HelmManagedDetector and ArgoManagedDetector for get_resource, pulled out so the
// "both present" case is testable — Argo owns, Helm is at most its renderer, but §5 pitfall 3 warns against
// just flipping `helmManaged.installed` to false, so both facts stay and only the note says who to act through.
internal static class ResourceOwnership
{
    private const string ArgoOwnsNote = "Argo CD owns this resource (see argoManaged) — Helm only rendered it; sync/history go through the argo_* tools, not helm_*.";

    public static void Annotate(JsonNode node)
    {
        if (HelmManagedDetector.Detect(node) is { } helmManaged)
        {
            node[HelmManagedDetector.PropertyName] = helmManaged;
        }

        if (ArgoManagedDetector.Detect(node) is not { } argoManaged)
        {
            return;
        }

        node[ArgoManagedDetector.PropertyName] = argoManaged;
        if (node[HelmManagedDetector.PropertyName]?["installed"]?.GetValue<bool>() == true)
        {
            node[HelmManagedDetector.PropertyName]!["note"] = ArgoOwnsNote;
        }
    }
}
