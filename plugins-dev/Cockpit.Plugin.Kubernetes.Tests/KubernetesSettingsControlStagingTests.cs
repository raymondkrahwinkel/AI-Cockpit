using System.Text.Json;
using Avalonia.Controls;
using Cockpit.Plugin.Kubernetes.Model;
using Cockpit.Plugin.Kubernetes.Settings;
using Cockpit.Plugin.Kubernetes.UI;
using Cockpit.Plugins.Abstractions.UI;
using NSubstitute;

namespace Cockpit.Plugin.Kubernetes.Tests;

// The settings view's half of the staged contract (AC-1003, sharpened by AC-1004): what it refuses, and that it
// writes nothing until the host runs the commit it handed back. This plugin is the one where that second half is
// worth pinning — its commit walks every row storing kubeconfigs through the secret layer and clearing the
// orphans, so a validate that wrote would be writing credentials the operator may still cancel.
//
// AC-1394: the view no longer takes a KubernetesSettings directly — it loads and saves a snapshot over the
// plugin's channel (Settings.ClusterSettingsChannel). `_Host` wires a substitute ICockpitUiHost straight to a
// real ClusterSettingsChannel bound to the test's own KubernetesSettings, so these tests still exercise the real
// load/save logic rather than a hand-rolled duplicate of it.
[Collection("avalonia")]
public class KubernetesSettingsControlStagingTests
{
    // Until AC-1004 a cluster whose label had been cleared was dropped in silence, kubeconfig and all — the save
    // reported success and the cluster was simply gone. A refusal can carry a reason now, so it does.
    [Fact]
    public void AClusterWithNoLabel_IsRefusedByPosition_AndNothingIsWritten()
    {
        var (host, settings) = _Host(
            new ClusterRegistration("cluster-1", "prod", string.Empty, [], KubeconfigPath: "~/.kube/config"),
            new ClusterRegistration("cluster-2", string.Empty, string.Empty, [], KubeconfigPath: "~/.kube/other"));
        var view = new KubernetesSettingsControl(host);

        var staged = view.TryStage(out var commit, out var error);

        Assert.False(staged);
        Assert.Null(commit);
        Assert.Contains("Cluster 2", error);
        Assert.Equal(2, settings.Clusters.Count);
    }

    [Fact]
    public void EveryClusterLabelled_Stages_AndOnlyTheCommitWrites()
    {
        var (host, settings) = _Host(new ClusterRegistration("cluster-1", "prod", string.Empty, [], KubeconfigPath: "~/.kube/config"));
        var view = new KubernetesSettingsControl(host);

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

    // A substitute ICockpitUiHost whose channel forwards "load-cluster-settings"/"save-cluster-settings" (the
    // action names Contracts.KubernetesChannel declares) to a real ClusterSettingsChannel over the returned
    // KubernetesSettings, so a test can inspect what actually got written the same way the production backend
    // would have written it. The action names are literals rather than KubernetesChannel's own constants: that
    // type is linked-compiled into both the backend and the UI assembly (AC-1394), and this project references
    // both, so the unqualified type name is ambiguous here.
    private static (ICockpitUiHost Host, KubernetesSettings Settings) _Host(params ClusterRegistration[] clusters)
    {
        var settings = new KubernetesSettings(new FakePluginStorage()) { Clusters = clusters };
        var channel = new ClusterSettingsChannel(settings);

        var host = Substitute.For<ICockpitUiHost>();
        // Unconfigured on a bare substitute this returns null, and the view adds it straight into an Avalonia
        // Controls collection (AC-1033's help hint) — a real host never returns null here (see CreateHelpHint's
        // own doc: invisible when there is no such article, never null).
        host.CreateHelpHint(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns(_ => new Panel());
        host.Channel.InvokeAsync("load-cluster-settings", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(_ => channel.LoadAsync());
        host.Channel.InvokeAsync("save-cluster-settings", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => channel.SaveAsync(callInfo.ArgAt<JsonElement>(1)));

        return (host, settings);
    }
}
