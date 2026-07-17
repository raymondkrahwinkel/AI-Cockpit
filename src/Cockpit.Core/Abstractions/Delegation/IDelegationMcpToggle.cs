namespace Cockpit.Core.Abstractions.Delegation;

/// <summary>
/// The live on/off for the orchestrator MCP (AC-40). Because that server is cockpit-hosted and no longer in the
/// operator's registry, the Options dialog toggles it here instead: the orchestrator answers <see cref="McpEnabled"/>
/// to the session fan-out, and <see cref="SetMcpEnabledAsync"/> both flips it live and persists the choice.
/// </summary>
public interface IDelegationMcpToggle
{
    /// <summary>Whether the orchestrator MCP is currently offered to sessions.</summary>
    bool McpEnabled { get; }

    /// <summary>Turns the orchestrator MCP on or off — takes effect on the next session's servers, and is persisted.</summary>
    Task SetMcpEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
}
