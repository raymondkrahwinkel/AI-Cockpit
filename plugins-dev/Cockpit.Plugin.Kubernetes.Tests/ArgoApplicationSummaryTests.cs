using System.Text.Json.Nodes;
using k8s;
using Cockpit.Plugin.Kubernetes.Mcp;

namespace Cockpit.Plugin.Kubernetes.Tests;

// AC-576 phase 1: exercised against literal Application payloads shaped after the 2026-08-25 measurement
// against a real Argo CD v3.3.2 cluster — including the healthy-cluster case (pitfall 6 in the ticket: a
// resource entry carries no "health" property at all unless something is actually wrong).
public class ArgoApplicationSummaryTests
{
    private const string HealthyApp = """
        {
          "metadata": {"name": "cert-manager"},
          "spec": {
            "project": "infra",
            "source": {"repoURL": "git@example.invalid:infra.git", "path": "cert-manager", "targetRevision": "HEAD"},
            "destination": {"server": "https://kubernetes.default.svc", "namespace": "system-secrets"},
            "syncPolicy": {"automated": {"prune": true, "selfHeal": true}}
          },
          "status": {
            "sync": {"status": "Synced", "revision": "a1b2c3d4e5f6789"},
            "health": {"status": "Healthy"},
            "sourceType": "Helm",
            "resources": [
              {"kind": "Deployment", "name": "cert-manager", "namespace": "system-secrets", "status": "Synced"},
              {"kind": "ServiceAccount", "name": "cert-manager", "namespace": "system-secrets", "status": "Synced"}
            ]
          }
        }
        """;

    private static RawKubernetesObject _Parse(string json) => KubernetesJson.Deserialize<RawKubernetesObject>(json);

    [Fact]
    public void SummarizeList_HealthyApp_HasNoHealthPropertyPerResource_AndAppLevelHealthStillReads()
    {
        const string listJson = """{"apiVersion":"argoproj.io/v1alpha1","kind":"ApplicationList","items":[""" + HealthyApp + "]}";
        var list = KubernetesJson.Deserialize<RawKubernetesList>(listJson);

        var summary = ArgoApplicationSummary.SummarizeList(list) as JsonObject;

        Assert.Equal(1, summary!["count"]!.GetValue<int>());
        var app = summary["applications"]![0]!;
        Assert.Equal("cert-manager", app["name"]!.GetValue<string>());
        Assert.Equal("infra", app["project"]!.GetValue<string>());
        Assert.Equal("Synced", app["syncStatus"]!.GetValue<string>());
        Assert.Equal("Healthy", app["health"]!.GetValue<string>());
        Assert.Equal("Helm", app["sourceType"]!.GetValue<string>());
        Assert.Equal(0, app["outOfSyncCount"]!.GetValue<int>());
        // A full commit sha is abbreviated, since the summary exists to stay small (criterion 3 below).
        Assert.Equal("a1b2c3d", app["revision"]!.GetValue<string>());
    }

    [Fact]
    public void SummarizeList_CountsOutOfSyncResources()
    {
        const string outOfSyncApp = """
            {"metadata":{"name":"drifted"},"spec":{"project":"infra"},"status":{
              "sync":{"status":"OutOfSync"},"health":{"status":"Degraded"},
              "resources":[
                {"kind":"Deployment","name":"a","status":"OutOfSync"},
                {"kind":"Service","name":"b","status":"Synced"},
                {"kind":"ConfigMap","name":"c","status":"OutOfSync"}
              ]}}
            """;
        var list = KubernetesJson.Deserialize<RawKubernetesList>(
            """{"apiVersion":"argoproj.io/v1alpha1","kind":"ApplicationList","items":[""" + outOfSyncApp + "]}");

        var app = (ArgoApplicationSummary.SummarizeList(list) as JsonObject)!["applications"]![0]!;

        Assert.Equal(2, app["outOfSyncCount"]!.GetValue<int>());
    }

    [Fact]
    public void SummarizeList_ThirtyFiveApps_StaysUnderEightKilobytes()
    {
        // AC-576 acceptance criterion 3, against get_resource's measured 51 KB for a single Application.
        var items = string.Join(',', Enumerable.Repeat(HealthyApp, 35));
        var list = KubernetesJson.Deserialize<RawKubernetesList>(
            """{"apiVersion":"argoproj.io/v1alpha1","kind":"ApplicationList","items":[""" + items + "]}");

        var bytes = ArgoApplicationSummary.SummarizeList(list).ToJsonString().Length;

        Assert.True(bytes < 8 * 1024, $"argo_apps for 35 Applications was {bytes} bytes, expected under 8 KB.");
    }

    [Fact]
    public void SummarizeApp_ReportsAutoSyncAndSelfHeal()
    {
        var app = ArgoApplicationSummary.SummarizeApp(_Parse(HealthyApp)) as JsonObject;

        Assert.True(app!["autoSync"]!.GetValue<bool>());
        Assert.True(app["selfHeal"]!.GetValue<bool>());
        Assert.Equal("git@example.invalid:infra.git", app["source"]!["repoURL"]!.GetValue<string>());
        Assert.Equal("HEAD", app["source"]!["targetRevision"]!.GetValue<string>());
        Assert.Equal("system-secrets", app["destination"]!["namespace"]!.GetValue<string>());
    }

    [Fact]
    public void SummarizeApp_NoSyncPolicy_IsAutoSyncFalse_NotAMissingField()
    {
        const string manual = """{"metadata":{"name":"manual"},"spec":{"project":"infra"},"status":{}}""";

        var app = ArgoApplicationSummary.SummarizeApp(_Parse(manual)) as JsonObject;

        Assert.False(app!["autoSync"]!.GetValue<bool>());
        Assert.False(app["selfHeal"]!.GetValue<bool>());
    }

    [Fact]
    public void SummarizeApp_HealthyResource_HasNullHealth_NotAThrow()
    {
        var app = ArgoApplicationSummary.SummarizeApp(_Parse(HealthyApp)) as JsonObject;

        var resources = app!["resources"]!.AsArray();
        Assert.Equal(2, resources.Count);
        Assert.Null(resources[0]!["health"]);
    }

    [Fact]
    public void SummarizeApp_DegradedResource_ReadsItsHealth()
    {
        const string degraded = """
            {"metadata":{"name":"a"},"spec":{"project":"infra"},"status":{
              "resources":[{"kind":"Deployment","name":"a","status":"Synced","health":{"status":"Degraded"}}]}}
            """;

        var app = ArgoApplicationSummary.SummarizeApp(_Parse(degraded)) as JsonObject;

        Assert.Equal("Degraded", app!["resources"]![0]!["health"]!.GetValue<string>());
    }

    [Fact]
    public void SummarizeHistory_ReadsRevisionDeployedAtAndInitiator()
    {
        const string withHistory = """
            {"metadata":{"name":"cert-manager"},"status":{"history":[
              {"revision":"aaa111","deployedAt":"2026-08-20T10:00:00Z","initiatedBy":{"username":"raymond"}},
              {"revision":"bbb222","deployedAt":"2026-08-21T10:00:00Z","initiatedBy":{"automated":true}}
            ]}}
            """;

        var history = (ArgoApplicationSummary.SummarizeHistory(_Parse(withHistory)) as JsonObject)!["history"]!.AsArray();

        Assert.Equal("aaa111", history[0]!["revision"]!.GetValue<string>());
        Assert.Equal("raymond", history[0]!["initiatedBy"]!.GetValue<string>());
        Assert.Equal("automated sync", history[1]!["initiatedBy"]!.GetValue<string>());
    }

    [Fact]
    public void SummarizeLastSync_NoOperationState_IsNullNotAThrow()
    {
        const string neverSynced = """{"metadata":{"name":"fresh"},"status":{}}""";

        var summary = ArgoApplicationSummary.SummarizeLastSync(_Parse(neverSynced)) as JsonObject;

        Assert.Null(summary!["operationState"]);
    }

    [Fact]
    public void SummarizeLastSync_ReadsPhaseAndPerResourceLine()
    {
        const string synced = """
            {"metadata":{"name":"cert-manager"},"status":{"operationState":{
              "phase":"Succeeded","message":"successfully synced","startedAt":"2026-08-25T08:00:00Z","finishedAt":"2026-08-25T08:00:05Z",
              "operation":{"initiatedBy":{"automated":true}},
              "syncResult":{"resources":[
                {"kind":"ServiceAccount","name":"cert-manager","namespace":"system-secrets","status":"Synced","message":"serviceaccount/cert-manager serverside-applied"}
              ]}}}}
            """;

        var summary = ArgoApplicationSummary.SummarizeLastSync(_Parse(synced)) as JsonObject;

        Assert.Equal("Succeeded", summary!["phase"]!.GetValue<string>());
        Assert.Equal("automated sync", summary["initiatedBy"]!.GetValue<string>());
        var resource = summary["resources"]!.AsArray()[0]!;
        Assert.Equal("serviceaccount/cert-manager serverside-applied", resource["message"]!.GetValue<string>());
    }
}
