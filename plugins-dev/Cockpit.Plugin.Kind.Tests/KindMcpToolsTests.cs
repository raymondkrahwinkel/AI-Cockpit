using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugin.Kind.Mcp;
using Cockpit.Plugin.Kind.Security;
using Cockpit.Plugin.Kind.Settings;
using NSubstitute;

namespace Cockpit.Plugin.Kind.Tests;

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

    // A denied create creates nothing; a denied delete leaves the cluster registered.
    [Theory]
    [InlineData("create", 0)]
    [InlineData("delete", 1)]
    public async Task Denied_LeavesTheRegistryAsItWas(string verb, int clustersAfter)
    {
        var (tools, settings, host) = _Tools(ConsentOutcome.Approved);
        if (verb == "delete")
        {
            await tools.KindCreate(AgentSuppliedSession, "cockpit-ac179");
        }

        host.RequestConsentAsync(Arg.Any<ConsentRequest>()).Returns(new ConsentDecision(ConsentOutcome.Denied));

        var response = verb == "create"
            ? await tools.KindCreate(AgentSuppliedSession, "cockpit-ac179")
            : await tools.KindDelete(AgentSuppliedSession, "cockpit-ac179");

        Assert.Contains("\"ok\":false", response);
        Assert.Equal(clustersAfter, settings.KindClusters.Count);
    }

    private static (KindMcpTools Tools, KindSettings Settings, ICockpitHost Host) _Tools(ConsentOutcome outcome)
    {
        var settings = new KindSettings(new FakePluginStorage());
        var host = Substitute.For<ICockpitHost>();
        host.RequestConsentAsync(Arg.Any<ConsentRequest>()).Returns(new ConsentDecision(outcome));
        host.CurrentMcpCallerPaneId.Returns(CallerPaneId);
        var cli = new FakeCliRunner();
        var directory = Directory.CreateTempSubdirectory("ac179-mcp-tests").FullName;
        var clusters = new KindClusterManager(settings, cli, new KindRuntime(cli), "kind", directory);
        return (new KindMcpTools(new KindConsentGate(host), clusters, host), settings, host);
    }
}
