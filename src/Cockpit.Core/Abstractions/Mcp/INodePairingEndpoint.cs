namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// Where a controller starts a pairing with this cockpit (AC-792) — the one address the operator reads off the
/// Security tab and types on the other machine. Null while the node master switch is off, or while this machine
/// has no LAN-facing address to advertise.
/// </summary>
/// <remarks>
/// One address rather than one per mounted MCP endpoint: the grant carries the endpoint list, so the controller
/// learns the rest for itself. That is the whole difference in effort between this and the hand-copying AC-790
/// shipped with.
/// </remarks>
public interface INodePairingEndpoint
{
    string? Address { get; }
}
