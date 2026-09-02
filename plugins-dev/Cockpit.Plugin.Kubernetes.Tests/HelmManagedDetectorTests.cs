using System.Text.Json.Nodes;
using Cockpit.Plugin.Kubernetes.Helm;

namespace Cockpit.Plugin.Kubernetes.Tests;

// AC-1061 phase 1 / AC-1068: get_resource must flag a Helm-managed resource without the caller having to know
// the raw label/annotation names — and must not claim a resource is an installed Helm release on the label
// alone — Argo CD's `helm template` sets that label without installing anything (AC-1068 AC1).
public class HelmManagedDetectorTests
{
    public static IEnumerable<object[]> Resources() =>
    [
        [
            """
            {
              "metadata": {
                "labels": { "app.kubernetes.io/managed-by": "Helm" },
                "annotations": {
                  "meta.helm.sh/release-name": "cert-manager",
                  "meta.helm.sh/release-namespace": "system-ingress"
                }
              }
            }
            """,
            true, "cert-manager", "system-ingress",
        ],
        // AC-1068 AC1 + AC4: the exact scenario measured on a real cluster (2026-08-25) — Argo CD rolled this out
        // with `helm template`, so it has the managed-by label and an Argo tracking-id but no `meta.helm.sh/release-*`
        // annotations. Before this fix, get_resource reported it as an installed Helm release with two empty strings.
        [
            """
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
            """,
            false, null!, null!,
        ],
        // Half the pair is not a release either: the name it did find is still reported, but `installed` stays
        // false — the caller is told what is there without being told it is a release.
        [
            """
            {
              "metadata": {
                "labels": { "app.kubernetes.io/managed-by": "Helm" },
                "annotations": { "meta.helm.sh/release-name": "cert-manager" }
              }
            }
            """,
            false, "cert-manager", null!,
        ],
        // Nothing to report on at all.
        ["""{ "metadata": { "labels": { "app": "web" } } }""", null!, null!, null!],
        ["""{ "metadata": { "labels": { "app.kubernetes.io/managed-by": "kubectl" } } }""", null!, null!, null!],
        ["{}", null!, null!, null!],
    ];

    [Theory]
    [MemberData(nameof(Resources))]
    public void Detect_ReportsAnInstalledReleaseOnlyWhenBothAnnotationsAreThere(
        string resource, bool? installed, string? releaseName, string? releaseNamespace)
    {
        var helmManaged = HelmManagedDetector.Detect(JsonNode.Parse(resource));

        Assert.Equal(installed, helmManaged?["installed"]?.GetValue<bool>());
        Assert.Equal(releaseName, helmManaged?["releaseName"]?.GetValue<string>());
        Assert.Equal(releaseNamespace, helmManaged?["releaseNamespace"]?.GetValue<string>());
    }
}
