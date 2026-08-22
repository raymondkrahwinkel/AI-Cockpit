namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// Where a controller starts a pairing with this cockpit (AC-792) — the address the operator reads off the Security
/// tab and types elsewhere. Null while the node master switch is off, or with no LAN-facing address. One address
/// rather than one per endpoint: the grant carries the endpoint list, so the controller learns the rest itself.
/// </summary>
public interface INodePairingEndpoint
{
    string? Address { get; }
}
