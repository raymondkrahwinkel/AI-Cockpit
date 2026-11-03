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

// AC-1061 phase 6 (AC 11): helm_upgrade goes through the helm CLI to render, and only for a path-registered
// cluster. These pin what reaches helm (argv, environment, values on stdin), that a pasted kubeconfig is refused
// before anything runs, and that a failed helm run is reported as a hint plus its raw stderr.
public class HelmUpgradeTests
{
    private const string Session = "pane-1";
    private const string DummyKubeconfig = "apiVersion: v1\nkind: Config\nclusters: []\ncontexts: []\nusers: []\n";

    private sealed class RecordingRunner(HelmResult result) : IHelmRunner
    {
        public HelmCommand? Command { get; private set; }

        public Task<HelmResult> RunAsync(HelmCommand command, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Command = command;
            return Task.FromResult(result);
        }
    }

    private static (KubernetesMcpTools Tools, RecordingRunner Runner, List<ConsentRequest> Asked) _Build(
        ConsentOutcome outcome, HelmResult? result = null, bool pathRegistered = true)
    {
        var cluster = new ClusterRegistration("id-1", "prod", ContextName: "kind-proof", ["default"], KubeconfigPath: pathRegistered ? "/tmp/ac1061.kubeconfig" : "");
        var settings = new KubernetesSettings(new FakePluginStorage());
        settings.Clusters = [cluster];
        if (!pathRegistered)
        {
            settings.SetKubeconfig(cluster.Id, DummyKubeconfig);
        }

        var asked = new List<ConsentRequest>();
        var host = Substitute.For<ICockpitHost>();
        host.RequestConsentAsync(Arg.Do<ConsentRequest>(asked.Add)).Returns(new ConsentDecision(outcome));

        var runner = new RecordingRunner(result ?? HelmResult.Exited(1, string.Empty, "Error: UPGRADE FAILED: \"proof\" has no deployed releases"));
        var tools = new KubernetesMcpTools(settings, new ClusterAccessGate(host), new ClusterConnectionFactory(settings), new PortForwardManager(), runner);
        return (tools, runner, asked);
    }

    [Fact]
    public async Task HelmUpgrade_AChartStartingWithADash_IsRefused_SoItCannotBecomeAnotherFlag()
    {
        var (tools, runner, _) = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.HelmUpgrade("prod", Session, "default", "proof", "--set=x=1"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Null(runner.Command);
    }

    [Fact]
    public async Task HelmUpgrade_RendersWithADryRun_AndPassesValuesOnStdinNotAsAFile()
    {
        var (tools, runner, _) = _Build(ConsentOutcome.Approved);

        await tools.HelmUpgrade("prod", Session, "default", "proof", "./chart", "1.2.3", "mode: changed\n");

        var command = Assert.IsType<HelmCommand>(runner.Command);
        Assert.Equal(
            ["upgrade", "--kube-context", "kind-proof", "--kubeconfig", "/tmp/ac1061.kubeconfig", "-n", "default", "proof", "./chart", "--dry-run=server", "--output", "json", "--version", "1.2.3", "--reuse-values", "-f", "-"],
            command.Arguments);
        Assert.Equal("mode: changed\n", command.StandardInput);
        Assert.Equal(string.Empty, command.Environment["HELM_KUBECONTEXT"]);
        Assert.Equal("secret", command.Environment["HELM_DRIVER"]);
    }

    [Fact]
    public async Task HelmUpgrade_WhenConsentIsDenied_HelmIsNeverRun()
    {
        var (tools, runner, _) = _Build(ConsentOutcome.Denied);

        var json = JsonNode.Parse(await tools.HelmUpgrade("prod", Session, "default", "proof", "./chart"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Null(runner.Command);
    }

    [Fact]
    public async Task HelmUpgrade_RenderingTheUpgrade_IsGatedAsCredentialMaterial()
    {
        var (tools, _, asked) = _Build(ConsentOutcome.Approved);

        await tools.HelmUpgrade("prod", Session, "default", "proof", "./chart");

        var request = Assert.Single(asked, candidate => candidate.Scope.StartsWith("k8s.secret:", StringComparison.Ordinal));
        Assert.Equal(ConsentRisk.Dangerous, request.Risk);
        Assert.False(request.AllowRemember);
    }

    [Fact]
    public void HelmFailure_ReadsTheCasesHelmCannotDistinguishByExitCode()
    {
        Assert.Contains("cluster could not be reached", HelmFailure.Describe(HelmResult.Exited(1, "", "Error: UPGRADE FAILED: kubernetes cluster unreachable: dial tcp"), "helm"));
        Assert.Contains("failed to render", HelmFailure.Describe(HelmResult.Exited(1, "", "Error: UPGRADE FAILED: parse error at (proof/templates/x.yaml:1)"), "helm"));
        Assert.Contains("did not finish in time", HelmFailure.Describe(HelmResult.Timeout, "helm"));
    }

}
