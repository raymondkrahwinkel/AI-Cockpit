namespace Cockpit.Core.Delegation;

/// <summary>
/// The operator's settings for delegation (AC-40), persisted under the <c>delegation</c> section of
/// <c>cockpit.json</c>. Delegation is a cockpit-hosted MCP the manager no longer lists, so this is where its
/// availability is turned on or off instead.
/// </summary>
public sealed record DelegationSettings
{
    /// <summary>Whether the orchestrator MCP is offered to sessions. On by default — delegation is a core capability.</summary>
    public bool McpEnabled { get; init; } = true;
}
