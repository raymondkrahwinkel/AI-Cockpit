namespace Cockpit.Core.Mcp;

// AC-790: network-node master switch, off by default so a Cockpit nobody meant as a node stays on loopback
// only. Enabling it adds an HTTPS listener guarded by SharedSecret (a persisted credential, unlike the
// ephemeral McpAuthKey, per AC-354) and takes effect only on next launch, not dynamically.
public sealed record NodeEndpointSettings
{
    public bool Enabled { get; init; }
    public string SharedSecret { get; init; } = "";

    // AC-792: who this node is paired with, or null. Null with a non-empty `SharedSecret` is a valid state — a
    // node whose operator set the secret by hand (AC-790) rather than via pairing.
    public NodePairing? Pairing { get; init; }

    // AC-793: CIDR ranges allowed to see this node from outside its own local network, e.g. "203.0.113.0/24".
    // Empty by default (own subnet always visible). Read by `INodeVisibilityPolicy` for discovery and by
    // `NodePairingHost` to gate `/pair/request`.
    public IReadOnlyList<string> AllowedDiscoveryRanges { get; init; } = [];

    public static NodeEndpointSettings Default { get; } = new();
}
