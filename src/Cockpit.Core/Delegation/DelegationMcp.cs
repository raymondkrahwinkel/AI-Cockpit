namespace Cockpit.Core.Delegation;

// Facts about the orchestrator's MCP server (#67) that both the host and the spawn paths need.
public static class DelegationMcp
{
    // The registry/server name; a session sees its tools as `mcp__cockpit-orchestrator__delegate_task` and friends.
    public const string ServerName = "cockpit-orchestrator";
}
