using System.Text.Json.Nodes;
using Cockpit.Plugin.Kubernetes.Helm;

namespace Cockpit.Plugin.Kubernetes.Tests;

// AC-1061 fase 1, AC2: get_resource must flag a Helm-managed resource without the caller having to know the raw
// label/annotation names.
public class HelmManagedDetectorTests
{
    [Fact]
    public void Detect_ManagedByHelmLabel_ReturnsReleaseNameAndNamespace()
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
        Assert.Equal("cert-manager", helmManaged!["releaseName"]!.GetValue<string>());
        Assert.Equal("system-ingress", helmManaged["releaseNamespace"]!.GetValue<string>());
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
