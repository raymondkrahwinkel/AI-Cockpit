using System.Text.Json.Nodes;
using Cockpit.Plugin.Kubernetes.Argo;

namespace Cockpit.Plugin.Kubernetes.Tests;

// AC-576 phase 2: exercised against the exact tracking-id shape measured on a real cluster (2026-08-25),
// `<app>:<group>/<kind>:<namespace>/<name>`, plus the instance-label fallback for pre-v3.3.2 tracking methods.
public class ArgoManagedDetectorTests
{
    [Fact]
    public void Detect_TrackingIdAnnotation_ReturnsTheOwningApplication()
    {
        var resource = JsonNode.Parse("""
            {
              "metadata": {
                "annotations": { "argocd.argoproj.io/tracking-id": "cert-manager:apps/Deployment:system-secrets/cert-manager" }
              }
            }
            """);

        var argoManaged = ArgoManagedDetector.Detect(resource);

        Assert.NotNull(argoManaged);
        Assert.Equal("cert-manager", argoManaged!["application"]!.GetValue<string>());
        Assert.Equal("tracking-id", argoManaged["source"]!.GetValue<string>());
    }

    [Fact]
    public void Detect_NoTrackingId_FallsBackToInstanceLabel()
    {
        var resource = JsonNode.Parse("""
            { "metadata": { "labels": { "app.kubernetes.io/instance": "cert-manager" } } }
            """);

        var argoManaged = ArgoManagedDetector.Detect(resource);

        Assert.NotNull(argoManaged);
        Assert.Equal("cert-manager", argoManaged!["application"]!.GetValue<string>());
        Assert.Equal("instance-label", argoManaged["source"]!.GetValue<string>());
    }

    [Fact]
    public void Detect_TrackingIdPresent_PrefersItOverTheInstanceLabel()
    {
        var resource = JsonNode.Parse("""
            {
              "metadata": {
                "labels": { "app.kubernetes.io/instance": "instance-label-app" },
                "annotations": { "argocd.argoproj.io/tracking-id": "tracking-id-app:apps/Deployment:ns/name" }
              }
            }
            """);

        var argoManaged = ArgoManagedDetector.Detect(resource);

        Assert.Equal("tracking-id-app", argoManaged!["application"]!.GetValue<string>());
    }

    [Fact]
    public void Detect_NeitherPresent_ReturnsNull()
    {
        var resource = JsonNode.Parse("""{ "metadata": { "labels": { "app": "web" } } }""");

        Assert.Null(ArgoManagedDetector.Detect(resource));
    }

    [Fact]
    public void Detect_NoMetadata_ReturnsNull() =>
        Assert.Null(ArgoManagedDetector.Detect(JsonNode.Parse("{}")));
}
