namespace Cockpit.Core.Delegation;

/// <summary>Facts about the orchestrator's MCP server (#67) that both the host and the spawn paths need.</summary>
public static class DelegationMcp
{
    /// <summary>The registry/server name; a session sees its tools as <c>mcp__cockpit-orchestrator__delegate_task</c> and friends.</summary>
    public const string ServerName = "cockpit-orchestrator";
}
