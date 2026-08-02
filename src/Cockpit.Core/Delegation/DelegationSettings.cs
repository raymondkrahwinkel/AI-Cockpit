namespace Cockpit.Core.Delegation;

// The operator's settings for delegation (AC-40), persisted under the `delegation` section of
// `cockpit.json`. Delegation is a cockpit-hosted MCP the manager no longer lists, so this is where its
// availability is turned on or off instead.
public sealed record DelegationSettings
{
    // Whether the orchestrator MCP is offered to sessions. On by default — delegation is a core capability.
    public bool McpEnabled { get; init; } = true;
}
