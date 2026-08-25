using System.Text.Json.Nodes;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugin.Kubernetes.Cluster;
using Cockpit.Plugin.Kubernetes.Helm;
using Cockpit.Plugin.Kubernetes.Mcp;
using Cockpit.Plugin.Kubernetes.Model;
using Cockpit.Plugin.Kubernetes.Security;
using Cockpit.Plugin.Kubernetes.Settings;
using NSubstitute;

namespace Cockpit.Plugin.Kubernetes.Tests;

// The half of helm_upgrade no unit test can reach: what a real helm binary makes of the revision we write, and what
// a real apiserver makes of our field ownership (AC-1061). Phase 2 found two bugs here that round-tripped perfectly
// through our own code. Off unless COCKPIT_HELM_KIND_KUBECONFIG points at a kind cluster — never run against one.
public class HelmUpgradeClusterTests
{
    private const string Session = "pane-1";
    private const string Release = "cockpit-ac1061";
    private static readonly TimeSpan HelmTimeout = TimeSpan.FromMinutes(2);

    private const string ConfigMapTemplate = """
        apiVersion: v1
        kind: ConfigMap
        metadata:
          name: cockpit-ac1061
        data:
          mode: "{{ .Values.mode }}"
        ---
        apiVersion: rbac.authorization.k8s.io/v1
        kind: ClusterRole
        metadata:
          name: cockpit-ac1061-reader
          annotations:
            cockpit.test/mode: "{{ .Values.mode }}"
        rules:
          - apiGroups: [""]
            resources: ["configmaps"]
            verbs: ["get"]
        """;

    [Fact]
    public async Task HelmUpgrade_AgainstARealHelmAndCluster_LeavesARevisionHelmItselfCanReadAndKeepUsing()
    {
        if (_Kubeconfig() is not { } kubeconfig)
        {
            return;
        }

        var chart = _WriteChart();
        var cluster = new ClusterRegistration("id-1", "kind", _Context(kubeconfig), ["default"], AllowClusterScoped: true, KubeconfigPath: kubeconfig);
        var settings = new KubernetesSettings(new FakePluginStorage());
        settings.Clusters = [cluster];
        var host = Substitute.For<ICockpitHost>();
        host.RequestConsentAsync(Arg.Any<ConsentRequest>()).Returns(new ConsentDecision(ConsentOutcome.Approved));
        var runner = new HelmRunner();
        var tools = new KubernetesMcpTools(settings, new ClusterAccessGate(host), new ClusterConnectionFactory(settings), new PortForwardManager(), TestKindClusters.Unused(settings), host, runner);

        await _HelmAsync(runner, cluster, ["uninstall", Release, "--ignore-not-found"]);
        var installed = await _HelmAsync(runner, cluster, ["install", Release, chart, "--set", "mode=before"]);
        Assert.True(installed.Succeeded, installed.Stderr);

        try
        {
            var json = JsonNode.Parse(await tools.HelmUpgrade("kind", Session, "default", Release, chart, values: "mode: after\n"));

            Assert.True(json!["ok"]!.GetValue<bool>(), json.ToJsonString());
            Assert.Equal(2, json["newRevision"]!.GetValue<int>());
            Assert.Equal(HelmReleaseLedger.Deployed, json["status"]!.GetValue<string>());

            // Phase 2 pitfall 1: helm parses its own timestamps out of the raw JSON bytes, so a revision we wrote
            // can be invisible to `helm history` while decoding perfectly for us.
            var history = await _HelmAsync(runner, cluster, ["history", Release, "--output", "json"]);
            Assert.True(history.Succeeded, history.Stderr);
            var revisions = JsonNode.Parse(history.Stdout)!.AsArray();
            Assert.Equal(2, revisions.Count);
            Assert.Contains(revisions, revision => revision!["revision"]!.GetValue<int>() == 2 && revision["status"]!.GetValue<string>() == HelmReleaseLedger.Deployed);

            // Phase 2 pitfall 2: without helm's own field manager the apiserver books our fields on "unknown" and
            // helm's next server-side apply refuses with a conflict.
            var afterUs = await _HelmAsync(runner, cluster, ["upgrade", Release, chart, "--set", "mode=byhelm"]);
            Assert.True(afterUs.Succeeded, afterUs.Stderr);

            // The rollback shares this apply path, so it is checked here too: helm must still be able to work with
            // the release afterwards.
            var rolledBack = JsonNode.Parse(await tools.HelmRollback("kind", Session, "default", Release, 2));
            Assert.True(rolledBack!["ok"]!.GetValue<bool>(), rolledBack.ToJsonString());
            var afterRollback = await _HelmAsync(runner, cluster, ["upgrade", Release, chart, "--set", "mode=byhelmagain"]);
            Assert.True(afterRollback.Succeeded, afterRollback.Stderr);
        }
        finally
        {
            await _HelmAsync(runner, cluster, ["uninstall", Release, "--ignore-not-found"]);
        }
    }

    private static Task<HelmResult> _HelmAsync(IHelmRunner runner, ClusterRegistration cluster, IReadOnlyList<string> argv)
    {
        var (command, error) = HelmCommand.Build("helm", cluster, "default", argv[0], argv.Skip(1).ToList());
        Assert.Null(error);
        return runner.RunAsync(command!, HelmTimeout);
    }

    private static string _WriteChart()
    {
        var directory = Directory.CreateTempSubdirectory("ac1061-chart");
        Directory.CreateDirectory(Path.Combine(directory.FullName, "templates"));
        File.WriteAllText(Path.Combine(directory.FullName, "Chart.yaml"), "apiVersion: v2\nname: cockpit-ac1061\nversion: 0.1.0\nappVersion: \"1.0\"\n");
        File.WriteAllText(Path.Combine(directory.FullName, "values.yaml"), "mode: base\n");
        File.WriteAllText(Path.Combine(directory.FullName, "templates", "resources.yaml"), ConfigMapTemplate);
        return directory.FullName;
    }

    // Only a kind cluster: this test installs, upgrades and uninstalls a release, which must never happen against
    // anything real. The guard is the context name, because that is what actually decides where the calls land.
    private static string? _Kubeconfig()
    {
        var kubeconfig = Environment.GetEnvironmentVariable("COCKPIT_HELM_KIND_KUBECONFIG");
        return string.IsNullOrWhiteSpace(kubeconfig) || !File.Exists(kubeconfig) || !_Context(kubeconfig).StartsWith("kind-", StringComparison.Ordinal)
            ? null
            : kubeconfig;
    }

    private static string _Context(string kubeconfig) =>
        File.ReadAllLines(kubeconfig)
            .Where(line => line.StartsWith("current-context:", StringComparison.Ordinal))
            .Select(line => line["current-context:".Length..].Trim())
            .FirstOrDefault() ?? string.Empty;
}
