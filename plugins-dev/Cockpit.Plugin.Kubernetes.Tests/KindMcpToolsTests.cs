using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugin.Kubernetes.Cluster;
using Cockpit.Plugin.Kubernetes.Kind;
using Cockpit.Plugin.Kubernetes.Mcp;
using Cockpit.Plugin.Kubernetes.Security;
using Cockpit.Plugin.Kubernetes.Settings;
using NSubstitute;

namespace Cockpit.Plugin.Kubernetes.Tests;

// The three kind_* MCP tools (AC-179) against a fake CliRunner-backed KindClusterManager — proves the consent-gate
// wiring and the transport-verified owner attribution, without a real kind binary.
public class KindMcpToolsTests
{
    private const string CallerPaneId = "pane-verified";
    private const string AgentSuppliedSession = "pane-claimed-by-agent";

    [Fact]
    public async Task KindCreate_Approved_CreatesAndOwnsItByTheTransportVerifiedPane()
    {
        var (tools, settings, host) = _Tools(ConsentOutcome.Approved);

        var response = await tools.KindCreate(AgentSuppliedSession, "cockpit-ac179");

        Assert.Contains("\"ok\":true", response);
        var record = Assert.Single(settings.KindClusters);
        Assert.Equal(CallerPaneId, record.OwnerPaneId);
        await host.Received(1).RequestConsentAsync(Arg.Is<ConsentRequest>(request => request.Risk == ConsentRisk.Dangerous && !request.AllowRemember));
    }

    [Fact]
    public async Task KindCreate_ShowsTheLiteralKindCommandOnTheConsentCard()
    {
        var (tools, _, host) = _Tools(ConsentOutcome.Approved);

        await tools.KindCreate(AgentSuppliedSession, "cockpit-ac179");

        await host.Received(1).RequestConsentAsync(Arg.Is<ConsentRequest>(request => request.Action.Contains("kind create cluster --name cockpit-ac179")));
    }

    [Fact]
    public async Task KindCreate_Denied_CreatesNothing()
    {
        var (tools, settings, _) = _Tools(ConsentOutcome.Denied);

        var response = await tools.KindCreate(AgentSuppliedSession, "cockpit-ac179");

        Assert.Contains("\"ok\":false", response);
        Assert.Empty(settings.KindClusters);
    }

    [Fact]
    public async Task KindList_ReturnsRegisteredClustersAsJson()
    {
        var (tools, settings, _) = _Tools(ConsentOutcome.Approved);
        settings.KindClusters = [new KindClusterRecord("cockpit-ac179", CallerPaneId, "/tmp/x.kubeconfig", DateTimeOffset.UtcNow)];

        var response = await tools.KindList();

        Assert.Contains("cockpit-ac179", response);
        Assert.Contains(CallerPaneId, response);
    }

    [Fact]
    public async Task KindDelete_Approved_RemovesTheRegisteredCluster()
    {
        var (tools, settings, _) = _Tools(ConsentOutcome.Approved);
        await tools.KindCreate(AgentSuppliedSession, "cockpit-ac179");

        var response = await tools.KindDelete(AgentSuppliedSession, "cockpit-ac179");

        Assert.Contains("\"ok\":true", response);
        Assert.Empty(settings.KindClusters);
    }

    [Fact]
    public async Task KindDelete_Denied_LeavesTheClusterRegistered()
    {
        var (approveTools, settings, host) = _Tools(ConsentOutcome.Approved);
        await approveTools.KindCreate(AgentSuppliedSession, "cockpit-ac179");
        host.RequestConsentAsync(Arg.Any<ConsentRequest>()).Returns(new ConsentDecision(ConsentOutcome.Denied));

        var response = await approveTools.KindDelete(AgentSuppliedSession, "cockpit-ac179");

        Assert.Contains("\"ok\":false", response);
        Assert.Single(settings.KindClusters);
    }

    [Fact]
    public async Task KindDelete_UnregisteredName_RefusesWithoutAskingTheOperator()
    {
        var (tools, _, host) = _Tools(ConsentOutcome.Approved);

        var response = await tools.KindDelete(AgentSuppliedSession, "not-registered");

        Assert.Contains("\"ok\":false", response);
        // The gate still asks (consent is per-attempt, not per-existing-cluster) — what matters is the manager
        // itself refuses to run kind against a name it never registered (KindClusterManagerTests covers that path).
        await host.Received(1).RequestConsentAsync(Arg.Any<ConsentRequest>());
    }

    private static (KubernetesMcpTools Tools, KubernetesSettings Settings, ICockpitHost Host) _Tools(ConsentOutcome outcome)
    {
        var storage = new FakePluginStorage();
        var settings = new KubernetesSettings(storage);
        var host = Substitute.For<ICockpitHost>();
        host.RequestConsentAsync(Arg.Any<ConsentRequest>()).Returns(new ConsentDecision(outcome));
        host.CurrentMcpCallerPaneId.Returns(CallerPaneId);
        var gate = new ClusterAccessGate(host);
        var cli = new FakeCliRunner();
        var kindRuntime = new KindRuntime(cli);
        var directory = Directory.CreateTempSubdirectory("ac179-mcp-tests").FullName;
        var kindClusters = new KindClusterManager(settings, cli, kindRuntime, "kind", directory);
        var tools = new KubernetesMcpTools(settings, gate, new ClusterConnectionFactory(settings), new PortForwardManager(), kindClusters, host);
        return (tools, settings, host);
    }
}
