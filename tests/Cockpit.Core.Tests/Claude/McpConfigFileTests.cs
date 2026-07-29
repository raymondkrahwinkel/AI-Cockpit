using FluentAssertions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Mcp;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// Locks <see cref="McpConfigFile.IsAgentEligible"/> — the one predicate every fan-out route (the SDK adapter and
/// the TTY adapter) shares to decide which registry servers a coding agent sees. AC-380 removed the host-side
/// <c>SerializeRegistryOnly</c> serializer this file used to lock instead: it had no production caller, since both
/// routes hand their eligible servers to a provider plugin's own spawn-config builder rather than to a
/// host-produced JSON body.
/// </summary>
public class McpConfigFileTests
{
    [Fact]
    public void ServerName_IsCockpit_SoTheReservedKeyIsNeverClaimedByTheRegistry()
    {
        McpConfigFile.ServerName.Should().Be("cockpit");
    }

    [Fact]
    public void IsAgentEligible_AnEnabledNonLocalNonReservedServer_IsEligible()
    {
        var server = new McpServerConfig { Name = "remote", Transport = McpTransport.Http, Url = "https://host/mcp" };

        McpConfigFile.IsAgentEligible(server).Should().BeTrue();
    }

    [Fact]
    public void IsAgentEligible_ADisabledServer_IsNotEligible()
    {
        var server = new McpServerConfig { Name = "off", Transport = McpTransport.Stdio, Command = "npx", Enabled = false };

        McpConfigFile.IsAgentEligible(server).Should().BeFalse();
    }

    [Fact]
    public void IsAgentEligible_ALocalOnlyServer_IsNotEligible()
    {
        // Local-model-only servers are noise for an agentic CLI: Claude Code/Codex already ship their own
        // file/shell/web tools.
        var server = new McpServerConfig { Name = "local", Transport = McpTransport.Stdio, Command = "npx", Scope = McpServerScope.LocalOnly };

        McpConfigFile.IsAgentEligible(server).Should().BeFalse();
    }

    [Fact]
    public void IsAgentEligible_TheReservedCockpitKey_IsNotEligible()
    {
        var server = new McpServerConfig { Name = McpConfigFile.ServerName, Transport = McpTransport.Http, Url = "https://evil/mcp" };

        McpConfigFile.IsAgentEligible(server).Should().BeFalse();
    }

    [Fact]
    public void IsAgentEligible_AClaudeOnlyScopedServer_IsEligible()
    {
        var server = new McpServerConfig { Name = "keep", Transport = McpTransport.Http, Url = "https://x/mcp", Scope = McpServerScope.ClaudeOnly };

        McpConfigFile.IsAgentEligible(server).Should().BeTrue();
    }
}
