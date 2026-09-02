using System.Text.Json.Nodes;
using Cockpit.Plugin.Kubernetes.Argo;

namespace Cockpit.Plugin.Kubernetes.Tests;

// AC-576 phase 2: exercised against the exact tracking-id shape measured on a real cluster (2026-08-25),
// `<app>:<group>/<kind>:<namespace>/<name>`, plus the instance-label fallback for pre-v3.3.2 tracking methods.
// One resource shape per row: the detector reads one blob of metadata and answers which application owns it and
// which signal said so, so the shapes are values of one behaviour rather than behaviours of their own. A null
// expectation means the detector must claim nothing at all.
public class ArgoManagedDetectorTests
{
    public static IEnumerable<object[]> Resources() =>
    [
        [
            """{ "metadata": { "annotations": { "argocd.argoproj.io/tracking-id": "cert-manager:apps/Deployment:system-secrets/cert-manager" } } }""",
            "cert-manager", "tracking-id",
        ],
        [
            """{ "metadata": { "labels": { "app.kubernetes.io/instance": "cert-manager" } } }""",
            "cert-manager", "instance-label",
        ],
        // The tracking-id wins over the instance label whenever both are there.
        [
            """
            {
              "metadata": {
                "labels": { "app.kubernetes.io/instance": "instance-label-app" },
                "annotations": { "argocd.argoproj.io/tracking-id": "tracking-id-app:apps/Deployment:ns/name" }
              }
            }
            """,
            "tracking-id-app", "tracking-id",
        ],
        // AC-1068's mistake in reverse: app.kubernetes.io/instance is a generic recommended label most Helm
        // charts also set to the release name — a real Helm release must not be misread as Argo-owned.
        [
            """
            {
              "metadata": {
                "labels": { "app.kubernetes.io/instance": "cert-manager" },
                "annotations": {
                  "meta.helm.sh/release-name": "cert-manager",
                  "meta.helm.sh/release-namespace": "system-secrets"
                }
              }
            }
            """,
            null!, null!,
        ],
        // Half a Helm release is not one, so the instance-label fallback still applies.
        [
            """
            {
              "metadata": {
                "labels": { "app.kubernetes.io/instance": "cert-manager" },
                "annotations": { "meta.helm.sh/release-name": "cert-manager" }
              }
            }
            """,
            "cert-manager", "instance-label",
        ],
        ["""{ "metadata": { "labels": { "app": "web" } } }""", null!, null!],
        ["{}", null!, null!],
    ];

    [Theory]
    [MemberData(nameof(Resources))]
    public void Detect_NamesTheOwningApplication_AndTheSignalThatSaidSo(string resource, string? application, string? source)
    {
        var argoManaged = ArgoManagedDetector.Detect(JsonNode.Parse(resource));

        Assert.Equal(application, argoManaged?["application"]?.GetValue<string>());
        Assert.Equal(source, argoManaged?["source"]?.GetValue<string>());
    }
}
