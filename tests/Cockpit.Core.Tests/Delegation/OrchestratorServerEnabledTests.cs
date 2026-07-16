using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Delegation;
using FluentAssertions;

namespace Cockpit.Core.Tests.Delegation;

/// <summary>
/// The orchestrator MCP server is on by default (#67, AC-6): its tools — <c>add_profile</c> especially — have to
/// be there before any delegation target exists, or the first one could never be scaffolded. It is a cockpit-owned
/// system server, so it is (re)asserted enabled on every launch: a stale or forgotten disabled entry from an older
/// build must not silently turn delegation off — the operator kept reporting it read as off by default. Excluding it
/// from a single session is the New-session picker's per-session checkbox, not a persistent registry switch.
/// </summary>
public class OrchestratorServerEnabledTests
{
    [Fact]
    public void NeverRegisteredBefore_IsEnabled()
    {
        OrchestratorMcpServer.ShouldBeEnabled(existingEntry: null).Should().BeTrue();
    }

    [Fact]
    public void AlreadyEnabled_StaysEnabled()
    {
        var existing = new McpServerConfig { Name = "cockpit-orchestrator", Enabled = true };

        OrchestratorMcpServer.ShouldBeEnabled(existing).Should().BeTrue();
    }

    [Fact]
    public void PreviouslyDisabled_IsReEnabled_SoAStaleOffNeverTurnsDelegationOffByDefault()
    {
        var existing = new McpServerConfig { Name = "cockpit-orchestrator", Enabled = false };

        OrchestratorMcpServer.ShouldBeEnabled(existing).Should().BeTrue();
    }
}
