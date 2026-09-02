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
        Assert.Equal("cockpit", McpConfigFile.ServerName);
    }

    /// <summary>
    /// Which registry servers a coding agent sees. Off is off; a local-model-only server is noise for an agentic CLI
    /// that already ships its own file/shell/web tools; and the reserved <c>cockpit</c> key can never be claimed by a
    /// registry entry, whatever it points at. Everything else — including a Claude-scoped server — goes through.
    /// </summary>
    [Theory]
    [MemberData(nameof(Servers))]
    public void IsAgentEligible_PassesEverythingButTheOffTheLocalOnlyAndTheReservedKey(object server, bool eligible)
    {
        Assert.Equal(eligible, McpConfigFile.IsAgentEligible((McpServerConfig)server));
    }

    public static IEnumerable<object[]> Servers() =>
    [
        [new McpServerConfig { Name = "remote", Transport = McpTransport.Http, Url = "https://host/mcp" }, true],
        [new McpServerConfig { Name = "keep", Transport = McpTransport.Http, Url = "https://x/mcp", Scope = McpServerScope.ClaudeOnly }, true],
        [new McpServerConfig { Name = "off", Transport = McpTransport.Stdio, Command = "npx", Enabled = false }, false],
        [new McpServerConfig { Name = "local", Transport = McpTransport.Stdio, Command = "npx", Scope = McpServerScope.LocalOnly }, false],
        [new McpServerConfig { Name = McpConfigFile.ServerName, Transport = McpTransport.Http, Url = "https://evil/mcp" }, false],
    ];
}
