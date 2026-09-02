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
    public void Compute_AnIdenticalManifest_IsAllUnchanged()
    {
        var diff = ManifestDiff.Compute(TraefikRevision7, TraefikRevision7);

        Assert.True(diff.IsEmpty);
        Assert.Equal(1, diff.UnchangedCount);
    }

    [Fact]
    public void Compute_AResourceTheTargetRevisionNoLongerHas_IsADeletion()
    {
        var current = TraefikRevision7 + "\n---\napiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: extra\n  namespace: system-ingress\n";

        var diff = ManifestDiff.Compute(current, TraefikRevision6);

        var deleted = Assert.Single(diff.Deletions);
        Assert.Equal("v1 ConfigMap system-ingress/extra", deleted.Document.Display);
        Assert.Equal(2, diff.Changes.Count);
    }

    [Fact]
    public void Compute_AResourceOnlyTheTargetRevisionHas_IsACreation()
    {
        var target = TraefikRevision6 + "\n---\napiVersion: v1\nkind: Service\nmetadata:\n  name: traefik-metrics\n  namespace: system-ingress\n";

        var diff = ManifestDiff.Compute(TraefikRevision7, target);

        var created = Assert.Single(diff.Changes, change => change.Change == ManifestChangeKind.Created);
        Assert.Equal("v1 Service system-ingress/traefik-metrics", created.Document.Display);
    }

    [Fact]
    public void Compute_TheSameNameInAnotherNamespace_IsAnotherResource()
    {
        var current = "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: shared\n  namespace: a\n";
        var target = "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: shared\n  namespace: b\n";

        var diff = ManifestDiff.Compute(current, target);

        Assert.Single(diff.Deletions);
        Assert.Single(diff.Applied);
    }

    [Fact]
    public void Compute_OneManifestRenderingAResourceTwice_IsWarnedAbout()
    {
        var twice = "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: x\n---\napiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: x\n";

        var diff = ManifestDiff.Compute(twice, twice);

        Assert.Contains(diff.Warnings, warning => warning.Contains("more than once"));
    }

    [Fact]
    public void SplitAll_DocumentsWithoutIdentityAreSkippedRatherThanApplied()
    {
        var manifest = "---\n\n---\n# only a comment\n---\napiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: x\n";

        var documents = ManifestDocument.SplitAll(manifest, out var errors);

        Assert.Single(documents);
        Assert.Empty(errors);
    }

    [Fact]
    public void SplitAll_ADocumentMissingItsKind_IsReportedNotDropped()
    {
        var documents = ManifestDocument.SplitAll("apiVersion: v1\nmetadata:\n  name: x\n", out var errors);

        Assert.Empty(documents);
        Assert.Single(errors);
    }

    [Theory]
    [InlineData("replicas: 3", 3L)]
    [InlineData("replicas: -2", -2L)]
    public void ToJson_AnUnquotedNumber_StaysANumber(string line, long expected)
    {
        var json = _Document($"apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: x\nspec:\n  {line}\n");

        Assert.Equal(expected, json["spec"]!["replicas"]!.GetValue<long>());
    }

    [Fact]
    public void ToJson_AQuotedNumberStaysAString_AndAnOctalLookalikeIsNotReinterpreted()
    {
        var json = _Document("apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: x\ndata:\n  port: \"8080\"\n  build: 010\n");

        Assert.Equal("8080", json["data"]!["port"]!.GetValue<string>());
        Assert.Equal("010", json["data"]!["build"]!.GetValue<string>());
    }

    [Fact]
    public void ToJson_BooleansAndNullsKeepTheirType()
    {
        var json = _Document("apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: x\n  creationTimestamp: null\ndata:\n  on: true\n  spelled: \"true\"\n");

        Assert.True(json["data"]!["on"]!.GetValue<bool>());
        Assert.Equal("true", json["data"]!["spelled"]!.GetValue<string>());
        Assert.Null(json["metadata"]!["creationTimestamp"]);
    }

    [Fact]
    public void ToJson_ListsAndNestedMapsSurvive()
    {
        var json = _Document(TraefikRevision6);

        var args = json["spec"]!["template"]!["spec"]!["containers"]![0]!["args"]!.AsArray();
        Assert.Equal(5, args.Count);
        Assert.Equal("--entrypoints.web.address=:8000/tcp", args[0]!.GetValue<string>());
    }

    [Fact]
    public void ToConsentText_ABudgetSmallerThanTheDiff_SaysWhatItLeftOut()
    {
        var current = string.Join("\n---\n", Enumerable.Range(0, 40).Select(index => $"apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: c{index}\ndata:\n  value: old"));
        var target = string.Join("\n---\n", Enumerable.Range(0, 40).Select(index => $"apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: c{index}\ndata:\n  value: new"));

        var text = ManifestDiff.Compute(current, target).ToConsentText(400);

        Assert.True(text.Length < 600, $"the consent line must stay bounded, got {text.Length} characters");
        Assert.Contains("more resource(s)", text);
    }

    [Fact]
    public void ToConsentText_NamesEveryDeletionEvenWhenNothingElseChanges()
    {
        var current = TraefikRevision6 + "\n---\napiVersion: v1\nkind: PersistentVolumeClaim\nmetadata:\n  name: traefik-data\n";

        var text = ManifestDiff.Compute(current, TraefikRevision6).ToConsentText(3_500);

        Assert.Contains("DELETE v1 PersistentVolumeClaim traefik-data", text);
    }

    [Fact]
    public void Compute_AChangedLine_IsOneRemovalAndOneAddition_NotARewrite()
    {
        var current = "apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: d\nspec:\n  image: app:2.0\n  port: 80\n";
        var target = "apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: d\nspec:\n  image: app:1.9\n  port: 80\n";

        var change = Assert.Single(ManifestDiff.Compute(current, target).Changes);

        Assert.Equal(1, change.AddedLines);
        Assert.Equal(1, change.RemovedLines);
        Assert.Contains("-  image: app:2.0", change.Diff);
        Assert.Contains("+  image: app:1.9", change.Diff);
    }

    [Fact]
    public void Compute_ADocumentPastTheDiffCeiling_IsReportedAsAWholeReplacement_NotAsUnchanged()
    {
        var header = "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: big\ndata:\n";
        var current = header + string.Join("\n", Enumerable.Range(0, 900).Select(index => $"  k{index}: old{index}"));
        var target = header + string.Join("\n", Enumerable.Range(0, 900).Select(index => $"  k{index}: new{index}"));

        var change = Assert.Single(ManifestDiff.Compute(current, target).Changes);

        Assert.Equal(ManifestChangeKind.Updated, change.Change);
        Assert.Equal(900, change.AddedLines);
        Assert.Equal(900, change.RemovedLines);
    }

    [Fact]
    public void Applied_KeepsTheTargetManifestOrder_SoResourcesGoBackInTheOrderHelmStoredThem()
    {
        var target = string.Join("\n---\n", new[] { "a", "b", "c" }.Select(name => $"apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: {name}"));

        var applied = ManifestDiff.Compute(null, target).Applied.Select(change => change.Document.Name).ToList();

        Assert.Equal(["a", "b", "c"], applied);
    }

    private static JsonObject _Document(string yaml) =>
        Assert.Single(ManifestDocument.SplitAll(yaml, out _)).ToJson()!;
}
