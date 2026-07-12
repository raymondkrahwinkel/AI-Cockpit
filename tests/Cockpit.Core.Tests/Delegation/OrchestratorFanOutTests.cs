using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions.Permissions;
using FluentAssertions;

namespace Cockpit.Core.Tests.Delegation;

/// <summary>
/// The orchestrator (#67) reaches a session as an ordinary MCP server, so it has to survive both fan-out paths:
/// the SDK spawn (permission server + registry) and the interactive TTY (registry only). If it is dropped by
/// either, delegation is simply unavailable in that kind of session — which is exactly the failure that is easy
/// to miss, because nothing errors: the tools are just not there.
/// </summary>
public class OrchestratorFanOutTests
{
    private static readonly McpServerConfig Orchestrator = new()
    {
        Name = "cockpit-orchestrator",
        Transport = McpTransport.Http,
        Scope = McpServerScope.All,
        Url = "http://127.0.0.1:46503/mcp",
        Enabled = true,
    };

    [Fact]
    public void TheTtyFanOut_CarriesTheOrchestrator_WhenItIsEnabled()
    {
        // The embedded TUI gets the registry, so an enabled orchestrator must show up in its --mcp-config.
        var json = McpConfigFile.SerializeRegistryOnly([Orchestrator]);

        json.Should().NotBeNull();
        json.Should().Contain("cockpit-orchestrator").And.Contain("http://127.0.0.1:46503/mcp");
    }

    [Fact]
    public void TheSdkFanOut_CarriesTheOrchestrator_WhenItIsEnabled()
    {
        var json = McpConfigFile.Serialize("http://127.0.0.1:1234/mcp", [Orchestrator]);

        json.Should().Contain("cockpit-orchestrator");
    }

    [Fact]
    public void NeitherFanOut_CarriesIt_WhileItIsSwitchedOff()
    {
        // Off is off: the server is registered with every cockpit, but a session only gets the ability to spawn
        // work under other profiles once the operator turns it on.
        var disabled = Orchestrator with { Enabled = false };

        McpConfigFile.SerializeRegistryOnly([disabled]).Should().BeNull();
        McpConfigFile.Serialize("http://127.0.0.1:1234/mcp", [disabled]).Should().NotContain("cockpit-orchestrator");
    }
}
