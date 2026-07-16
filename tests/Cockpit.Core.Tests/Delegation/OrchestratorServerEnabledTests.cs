using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Delegation;
using FluentAssertions;

namespace Cockpit.Core.Tests.Delegation;

/// <summary>
/// The orchestrator MCP server is on by default (#67, AC-6): its tools — <c>add_profile</c> especially — have to
/// be there before any delegation target exists, or the first one could never be scaffolded. It used to be
/// rewritten to "enabled only if a target exists" on every start, which reset both the default and any manual
/// toggle — the reason it read as off every time. So on first registration it is on, and after that the operator's
/// own switch is left alone.
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
    public void TheOperatorTurnedItOff_StaysOff_NotRewrittenOnEveryStart()
    {
        var existing = new McpServerConfig { Name = "cockpit-orchestrator", Enabled = false };

        OrchestratorMcpServer.ShouldBeEnabled(existing).Should().BeFalse();
    }
}
