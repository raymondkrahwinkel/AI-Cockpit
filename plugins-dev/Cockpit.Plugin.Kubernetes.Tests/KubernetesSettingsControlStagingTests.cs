using Cockpit.Plugin.Kubernetes.Model;
using Cockpit.Plugin.Kubernetes.Settings;
using Cockpit.Plugin.Kubernetes.Ui;

namespace Cockpit.Plugin.Kubernetes.Tests;

// The settings view's half of the staged contract (AC-1003, sharpened by AC-1004): what it refuses, and that it
// writes nothing until the host runs the commit it handed back. This plugin is the one where that second half is
// worth pinning — its commit walks every row storing kubeconfigs through the secret layer and clearing the
// orphans, so a validate that wrote would be writing credentials the operator may still cancel.
[Collection("avalonia")]
public class KubernetesSettingsControlStagingTests
{
    // Until AC-1004 a cluster whose label had been cleared was dropped in silence, kubeconfig and all — the save
    // reported success and the cluster was simply gone. A refusal can carry a reason now, so it does.
    [Fact]
    public void AClusterWithNoLabel_IsRefusedByPosition_AndNothingIsWritten()
    {
        var settings = _Settings(
            new ClusterRegistration("cluster-1", "prod", string.Empty, [], KubeconfigPath: "~/.kube/config"),
            new ClusterRegistration("cluster-2", string.Empty, string.Empty, [], KubeconfigPath: "~/.kube/other"));
        var view = new KubernetesSettingsControl(new FakeCockpitHost(), settings);

        var staged = view.TryStage(out var commit, out var error);

        Assert.False(staged);
        Assert.Null(commit);
        Assert.Contains("Cluster 2", error);
        Assert.Equal(2, settings.Clusters.Count);
    }

    [Fact]
    public void EveryClusterLabelled_Stages_AndOnlyTheCommitWrites()
    {
        var settings = _Settings(new ClusterRegistration("cluster-1", "prod", string.Empty, [], KubeconfigPath: "~/.kube/config"));
        var view = new KubernetesSettingsControl(new FakeCockpitHost(), settings);

        // Changed behind the view's back, so the value it holds (read when it was built) differs from the stored
        // one: staging must leave this at false and only the commit put the view's own answer back.
        settings.McpEnabled = false;

        Assert.True(view.TryStage(out var commit, out var error));
        Assert.Null(error);
        Assert.False(settings.McpEnabled);

        commit!();

        Assert.True(settings.McpEnabled);
        Assert.Equal("prod", settings.Clusters.Single().Label);
    }

    private static KubernetesSettings _Settings(params ClusterRegistration[] clusters) =>
        new(new FakePluginStorage()) { Clusters = clusters };
}
