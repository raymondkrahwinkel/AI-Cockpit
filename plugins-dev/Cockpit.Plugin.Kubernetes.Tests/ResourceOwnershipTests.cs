using System.Text.Json.Nodes;
using Cockpit.Plugin.Kubernetes.Mcp;

namespace Cockpit.Plugin.Kubernetes.Tests;

// AC-576 phase 2, AC 2: when a resource carries both Argo CD's tracking-id and genuine Helm release
// annotations, Argo owns it and Helm is at most its renderer — the answer must say so without discarding
// either fact (§5 pitfall 3: flipping helmManaged to a bare false is too crude).
public class ResourceOwnershipTests
{
    [Fact]
    public void Annotate_ArgoOwnedRenderedByHelm_SetsBothFields_HelmReportsNotInstalled()
    {
        var node = JsonNode.Parse("""
            {
              "metadata": {
                "labels": { "app.kubernetes.io/managed-by": "Helm" },
                "annotations": { "argocd.argoproj.io/tracking-id": "cert-manager:apps/Deployment:system-secrets/cert-manager" }
              }
            }
            """)!;

        ResourceOwnership.Annotate(node);

        Assert.Equal("cert-manager", node["argoManaged"]!["application"]!.GetValue<string>());
        Assert.False(node["helmManaged"]!["installed"]!.GetValue<bool>());
    }

    [Fact]
    public void Annotate_ArgoOwnedButGenuineHelmAnnotationsAlsoPresent_KeepsBothFacts_NoteSaysArgoOwns()
    {
        var node = JsonNode.Parse("""
            {
              "metadata": {
                "labels": { "app.kubernetes.io/managed-by": "Helm" },
                "annotations": {
                  "argocd.argoproj.io/tracking-id": "cert-manager:apps/Deployment:system-secrets/cert-manager",
                  "meta.helm.sh/release-name": "cert-manager",
                  "meta.helm.sh/release-namespace": "system-secrets"
                }
              }
            }
            """)!;

        ResourceOwnership.Annotate(node);

        // Both facts survive — this is the pitfall-3 case: do not silently turn helmManaged.installed to
        // false just because Argo also owns it.
        Assert.True(node["helmManaged"]!["installed"]!.GetValue<bool>());
        Assert.Equal("cert-manager", node["helmManaged"]!["releaseName"]!.GetValue<string>());
        Assert.Equal("cert-manager", node["argoManaged"]!["application"]!.GetValue<string>());
        Assert.Contains("Argo CD owns this resource", node["helmManaged"]!["note"]!.GetValue<string>());
    }

    [Fact]
    public void Annotate_HelmOnly_SetsOnlyHelmManaged()
    {
        var node = JsonNode.Parse("""
            {
              "metadata": {
                "labels": {
                  "app.kubernetes.io/managed-by": "Helm",
                  "app.kubernetes.io/instance": "cert-manager"
                },
                "annotations": {
                  "meta.helm.sh/release-name": "cert-manager",
                  "meta.helm.sh/release-namespace": "system-secrets"
                }
              }
            }
            """)!;

        ResourceOwnership.Annotate(node);

        // `app.kubernetes.io/instance` is a generic recommended label a Helm chart sets too — a real Helm
        // release must not fall back into being read as Argo-owned just because it also carries it.
        Assert.NotNull(node["helmManaged"]);
        Assert.Null(node["argoManaged"]);
    }

    [Fact]
    public void Annotate_NeitherLabelPresent_SetsNeitherField()
    {
        var node = JsonNode.Parse("""{ "metadata": { "labels": { "app": "web" } } }""")!;

        ResourceOwnership.Annotate(node);

        Assert.Null(node["helmManaged"]);
        Assert.Null(node["argoManaged"]);
    }
}
