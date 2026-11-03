using System.Text.Json.Nodes;
using Cockpit.Plugin.Kubernetes.Helm;

namespace Cockpit.Plugin.Kubernetes.Tests;

// AC-1061 fase 2. This is the half of a rollback that fails silently instead of loudly: a diff that misses a change
// shows the operator an approval that is not what runs, and a mistyped scalar reaches the apiserver as a value the
// chart never meant. Every case here is one of those, including the traefik rev7 -> rev6 change the ticket measured.
public class ManifestDiffTests
{
    private const string TraefikRevision7 = """
        ---
        # Source: traefik/templates/deployment.yaml
        apiVersion: apps/v1
        kind: Deployment
        metadata:
          name: traefik
          namespace: system-ingress
        spec:
          replicas: 1
          template:
            spec:
              containers:
                - name: traefik
                  args:
                    - "--entrypoints.web.address=:8000/tcp"
                    - "--entrypoints.websecure.address=:8443/tcp"
        """;

    private const string TraefikRevision6 = """
        ---
        # Source: traefik/templates/deployment.yaml
        apiVersion: apps/v1
        kind: Deployment
        metadata:
          name: traefik
          namespace: system-ingress
        spec:
          replicas: 1
          template:
            spec:
              containers:
                - name: traefik
                  args:
                    - "--entrypoints.web.address=:8000/tcp"
                    - "--entrypoints.websecure.address=:8443/tcp"
                    - "--certificatesresolvers.letsencrypt.acme.email=ops@example.com"
                    - "--certificatesresolvers.letsencrypt.acme.storage=/data/acme.json"
                    - "--certificatesresolvers.letsencrypt.acme.tlschallenge=true"
        """;

    [Fact]
    public void Compute_TheTraefikRollback_ShowsExactlyTheThreeArgumentLines_AndNothingElse()
    {
        var diff = ManifestDiff.Compute(TraefikRevision7, TraefikRevision6);

        var change = Assert.Single(diff.Changes);
        Assert.Equal(ManifestChangeKind.Updated, change.Change);
        Assert.Equal("apps/v1 Deployment system-ingress/traefik", change.Document.Display);
        Assert.Equal(3, change.AddedLines);
        Assert.Equal(0, change.RemovedLines);

        var lines = change.Diff!.Split('\n');
        Assert.Equal(
        [
            "- \"--certificatesresolvers.letsencrypt.acme.email=ops@example.com\"",
            "- \"--certificatesresolvers.letsencrypt.acme.storage=/data/acme.json\"",
            "- \"--certificatesresolvers.letsencrypt.acme.tlschallenge=true\"",
        ], lines.Where(line => line.StartsWith('+')).Select(line => line[1..].Trim()));

        // The document is 17 lines; a three-line change must read as three lines plus context, never as a rewrite —
        // an operator approving a rollback has to see the change, not the whole file over again.
        Assert.DoesNotContain(lines, line => line.StartsWith('-'));
        Assert.True(lines.Length <= 9, $"expected a bounded hunk, got {lines.Length} lines");
    }

    [Fact]
    public void ToJson_AQuotedNumberStaysAString_AndAnOctalLookalikeIsNotReinterpreted()
    {
        var json = _Document("apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: x\ndata:\n  port: \"8080\"\n  build: 010\n");

        Assert.Equal("8080", json["data"]!["port"]!.GetValue<string>());
        Assert.Equal("010", json["data"]!["build"]!.GetValue<string>());
    }

    [Fact]
    public void ToConsentText_NamesEveryDeletionEvenWhenNothingElseChanges()
    {
        var current = TraefikRevision6 + "\n---\napiVersion: v1\nkind: PersistentVolumeClaim\nmetadata:\n  name: traefik-data\n";

        var text = ManifestDiff.Compute(current, TraefikRevision6).ToConsentText(3_500);

        Assert.Contains("DELETE v1 PersistentVolumeClaim traefik-data", text);
    }

    private static JsonObject _Document(string yaml) =>
        Assert.Single(ManifestDocument.SplitAll(yaml, out _)).ToJson()!;
}
