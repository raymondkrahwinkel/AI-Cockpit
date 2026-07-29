using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Delegation;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Sessions.Tty;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Delegation;

/// <summary>
/// The orchestrator (#67) reaches an interactive TTY session as an ordinary registry server, fanned out by
/// <see cref="PluginTtySessionProviderAdapter.BuildLaunch"/> onto <see cref="PluginTtyLaunchContext.McpServers"/> —
/// not by a host-side JSON serializer. (AC-380: an earlier <c>McpConfigFile.SerializeRegistryOnly</c> had no
/// production caller — the provider plugins build their own spawn config from the servers this adapter resolves —
/// and was removed; this test used to guard that dead path instead of the live one.) If the orchestrator is
/// dropped here, delegation is silently unavailable in that session: nothing errors, the tools are just not there,
/// and the system prompt nudge that tells the model to look for them never gets added either.
/// </summary>
public class OrchestratorFanOutTests
{
    private static McpServerConfig _Orchestrator(bool enabled = true) => new()
    {
        Name = DelegationMcp.ServerName,
        Transport = McpTransport.Http,
        Scope = McpServerScope.All,
        Url = "http://127.0.0.1:46503/mcp",
        Enabled = enabled,
    };

    private static (PluginTtySessionProviderAdapter Adapter, IPluginTtyProvider Inner) _CreateAdapter(IMcpServerCatalog catalog)
    {
        var inner = Substitute.For<IPluginTtyProvider>();
        inner.BuildLaunch(Arg.Any<PluginTtyLaunchContext>()).Returns(new PluginTtyLaunchSpec(
            "codex", [], new Dictionary<string, string?>(), "/wd", []));

        return (new PluginTtySessionProviderAdapter("cli-agent-provider.codex", inner, """{"Command":"codex"}""", catalog), inner);
    }

    [Fact]
    public void BuildLaunch_CarriesTheEnabledOrchestrator_IntoThePluginsMcpServers_WithTheDelegationPrompt()
    {
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<McpServerConfig> { _Orchestrator() });
        var (adapter, inner) = _CreateAdapter(catalog);
        var context = new TtyLaunchContext(null, new Dictionary<string, string>(), "/wd", null, new Dictionary<string, string>());

        adapter.BuildLaunch(context);

        inner.Received(1).BuildLaunch(Arg.Is<PluginTtyLaunchContext>(pluginContext =>
            pluginContext.McpServers.Count == 1
            && pluginContext.McpServers[0].Name == DelegationMcp.ServerName
            && pluginContext.McpServers[0].Url == "http://127.0.0.1:46503/mcp"
            && pluginContext.DelegationSystemPrompt == DelegationSystemPrompt.Default));
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
        var context = new TtyLaunchContext(null, new Dictionary<string, string>(), "/wd", null, new Dictionary<string, string>());

        adapter.BuildLaunch(context);

        inner.Received(1).BuildLaunch(Arg.Is<PluginTtyLaunchContext>(pluginContext =>
            pluginContext.McpServers.Count == 0 && pluginContext.DelegationSystemPrompt == null));
    }
}
