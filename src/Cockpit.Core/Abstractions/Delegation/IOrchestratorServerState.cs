namespace Cockpit.Core.Abstractions.Delegation;

/// <summary>
/// Where the orchestrator MCP server (#67) ended up listening, so a session that is allowed to delegate can be
/// pointed at it. Published once the server has bound its port.
/// </summary>
public interface IOrchestratorServerState
{
    /// <summary>The server's MCP endpoint, or <see langword="null"/> before it has started.</summary>
    string? OrchestratorMcpUrl { get; }
}
