using System.Text.Json.Nodes;
using Cockpit.Plugin.Kubernetes.Helm;

namespace Cockpit.Plugin.Kubernetes.Tests;

// AC-1061 phase 1 / AC-1068: get_resource must flag a Helm-managed resource without the caller having to know
// the raw label/annotation names — and must not claim a resource is an installed Helm release on the label
// alone, since Argo CD's `helm template` sets that same label without ever installing anything (AC-1068 AC1).
public class HelmManagedDetectorTests
{
    [Fact]
    public void Detect_LabelAndAnnotations_ReportsInstalledWithReleaseNameAndNamespace()
    {
        var resource = JsonNode.Parse("""
            {
              "metadata": {
                "labels": { "app.kubernetes.io/managed-by": "Helm" },
                "annotations": {
                  "meta.helm.sh/release-name": "cert-manager",
                  "meta.helm.sh/release-namespace": "system-ingress"
                }
              }
            }
            """);

        var helmManaged = HelmManagedDetector.Detect(resource);

        Assert.NotNull(helmManaged);
        Assert.True(helmManaged!["installed"]!.GetValue<bool>());
        Assert.Equal("cert-manager", helmManaged["releaseName"]!.GetValue<string>());
        Assert.Equal("system-ingress", helmManaged["releaseNamespace"]!.GetValue<string>());
    }

    // AC-1068 AC1 + AC4: the exact scenario measured on a real cluster (2026-08-25) — Argo CD rolled this out
    // with `helm template`, so it has the managed-by label and an Argo tracking-id but no `meta.helm.sh/release-*`
    // annotations. Before this fix, get_resource reported it as an installed Helm release with two empty strings.
    [Fact]
    public void Detect_LabelWithoutReleaseAnnotations_ReportsNotInstalled_NotEmptyStrings()
    {
        var resource = JsonNode.Parse("""
            {
              "metadata": {
                "labels": {
                  "app.kubernetes.io/managed-by": "Helm",
                  "app.kubernetes.io/instance": "cert-manager"
                },
                "annotations": {
                  "argocd.argoproj.io/tracking-id": "cert-manager:apps/Deployment:system-secrets/cert-manager"
                }
              }
            }
            """);

        var helmManaged = HelmManagedDetector.Detect(resource);

        Assert.NotNull(helmManaged);
        Assert.False(helmManaged!["installed"]!.GetValue<bool>());
        Assert.Null(helmManaged["releaseName"]);
        Assert.Null(helmManaged["releaseNamespace"]);
    }

    [Fact]
    public void Detect_LabelWithOnlyOneReleaseAnnotation_ReportsNotInstalled()
    {
        var resource = JsonNode.Parse("""
            {
              "metadata": {
                "labels": { "app.kubernetes.io/managed-by": "Helm" },
                "annotations": { "meta.helm.sh/release-name": "cert-manager" }
              }
            }
            """);

        var helmManaged = HelmManagedDetector.Detect(resource);

        Assert.False(helmManaged!["installed"]!.GetValue<bool>());
    }

    [Fact]
    public void Detect_NoManagedByLabel_ReturnsNull()
    {
        var resource = JsonNode.Parse("""{ "metadata": { "labels": { "app": "web" } } }""");

        Assert.Null(HelmManagedDetector.Detect(resource));
    }

    [Fact]
    public void Detect_ManagedByLabelWithADifferentValue_ReturnsNull()
    {
        var resource = JsonNode.Parse("""{ "metadata": { "labels": { "app.kubernetes.io/managed-by": "kubectl" } } }""");

        Assert.Null(HelmManagedDetector.Detect(resource));
    }

    [Fact]
    public void Detect_NoMetadata_ReturnsNull() =>
        Assert.Null(HelmManagedDetector.Detect(JsonNode.Parse("{}")));
}
