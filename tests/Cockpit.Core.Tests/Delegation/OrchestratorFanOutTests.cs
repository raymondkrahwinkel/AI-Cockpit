using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Delegation;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Sessions.Tty;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Delegation;

// Delegation availability and briefing preservation across SDK and TTY gateway launches.
public class OrchestratorFanOutTests : SessionMcpTestFixture
{
    private static McpServerConfig _Orchestrator(bool enabled = true) => new()
    {
        Name = DelegationMcp.ServerName,
        Transport = McpTransport.Http,
        Scope = McpServerScope.All,
        Url = "http://127.0.0.1:46503/mcp",
        Enabled = enabled,
    };

    private (PluginTtySessionProviderAdapter Adapter, IPluginTtyProvider Inner) _CreateAdapter(IMcpServerCatalog catalog)
    {
        var inner = Substitute.For<IPluginTtyProvider>();
        inner.BuildLaunch(Arg.Any<PluginTtyLaunchContext>()).Returns(new PluginTtyLaunchSpec(
            "codex", [], new Dictionary<string, string?>(), "/wd", []));

        return (new PluginTtySessionProviderAdapter("cli-agent-provider.codex", inner, """{"Command":"codex"}""", catalog, mcpMounts: Mounts, gateway: Gateway), inner);
    }

    [Fact]
    public void BuildLaunch_DropsTheOrchestratorAndTheDelegationPrompt_WhileItIsSwitchedOff()
    {
        // Off is off: the server is registered with every cockpit, but a session only gets the ability to spawn
        // work under other profiles once the operator turns it on.
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<McpServerConfig> { _Orchestrator(enabled: false) });
        var (adapter, inner) = _CreateAdapter(catalog);
        var context = new TtyLaunchContext(null, new Dictionary<string, string>(), "/wd", null, new Dictionary<string, string> { ["COCKPIT_PANE_ID"] = "tty", ["COCKPIT_MCP_KEY"] = Keyring.TokenFor("tty") });

        adapter.BuildLaunch(context);

        inner.Received(1).BuildLaunch(Arg.Is<PluginTtyLaunchContext>(pluginContext =>
            pluginContext.McpServers.Count == 1 && pluginContext.McpServers[0].Name == "cockpit" && pluginContext.DelegationSystemPrompt == null));
        Assert.False(Mounts.IsMounted("tty", DelegationMcp.ServerName));
    }

    private (PluginSessionDriverAdapter Adapter, IPluginSessionDriver Inner) _CreateHeadlessAdapter(IMcpServerCatalog catalog)
    {
        var inner = Substitute.For<IPluginSessionDriver>();
        return (
            new PluginSessionDriverAdapter(inner, new PluginSessionCapabilities(SupportsTools: true, SupportsPermissions: true), catalog, keyring: Keyring, mcpMounts: Mounts, gateway: Gateway),
            inner);
    }

    private static IMcpServerCatalog _CatalogWith(params McpServerConfig[] servers)
    {
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns([.. servers]);
        return catalog;
    }

    // AC-147: the headless half of the same fan-out. `Scope = All` put the orchestrator's tools in these sessions
    // all along; only the nudge that says they are worth reaching for stayed on Claude's TTY route, so elsewhere
    // they sat unused.

    // The point of this test is the word *appended*: an Autopilot step hands its agent a CEO brief on that same
    // key (AC-180), and a nudge that replaced it would cut the planning instructions out of the run.
    [Fact]
    public async Task StartAsync_AppendsTheDelegationNudge_WithoutDisplacingTheBriefingAlreadyOnThatKey()
    {
        var (adapter, inner) = _CreateHeadlessAdapter(_CatalogWith(_Orchestrator()));
        await using var session = adapter;

        await adapter.StartAsync(
            launchOptions: new Dictionary<string, string>
            {
                [WellKnownPluginSessionOptions.AppendSystemPrompt] = "You are the CEO. Plan the work first.",
            });

        var environment = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(
            Assert.Single(inner.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(IPluginSessionDriver.StartAsync)).GetArguments()[5]);
        var expected = "You are the CEO. Plan the work first.\n\n" + DelegationSystemPrompt.Default
            + "\n\n" + SessionScratch.Instruction(environment["COCKPIT_SCRATCH"]);

        await inner.Received(1).StartAsync(
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Is<IReadOnlyDictionary<string, string>?>(options =>
                options != null && options[WellKnownPluginSessionOptions.AppendSystemPrompt] == expected),
            Arg.Any<IReadOnlyList<PluginMcpServer>?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(),
            Arg.Any<IPluginToolset?>(), Arg.Any<CancellationToken>());
    }
}
