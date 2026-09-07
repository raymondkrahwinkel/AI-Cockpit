namespace Cockpit.Core.Mcp;

// AC-790: network-node master switch, off by default so a Cockpit nobody meant as a node stays on loopback
// only. Enabling it adds an HTTPS listener guarded by SharedSecret (a persisted credential, unlike the
// ephemeral McpAuthKey, per AC-354) and takes effect only on next launch, not dynamically.
public sealed record NodeEndpointSettings
{
    // AC-1284: the pairing listener's port. Fixed rather than OS-assigned, because a controller freezes the
    // node's address in its MCP registry at pairing time: with an ephemeral port the node's first restart moved
    // it and broke the coupling for good. 0 still means "let the OS pick", which is what the tests use.
    public const int DefaultPort = 52382;

    // The node's MCP listener sits directly above the pairing listener. One adjacent pair rather than a second
    // setting: the operator opens or publishes two consecutive ports, and a controller holding an MCP URL can
    // work back to the pairing port — see `NodeSessionsClient`, which re-resolves a moved node that way.
    public const int McpPortOffset = 1;

    public bool Enabled { get; init; }
    public string SharedSecret { get; init; } = "";

    public int Port { get; init; } = DefaultPort;

    // Follows Port when that is 0, so "let the OS pick" keeps meaning both listeners rather than one fixed and
    // one not.
    public int McpPort => Port == 0 ? 0 : Port + McpPortOffset;

    // AC-792: who this node is paired with, or null. Null with a non-empty `SharedSecret` is a valid state — a
    // node whose operator set the secret by hand (AC-790) rather than via pairing.
    public NodePairing? Pairing { get; init; }

    // AC-793: CIDR ranges allowed to see this node from outside its own local network, e.g. "203.0.113.0/24".
    // Empty by default (own subnet always visible). Read by `INodeVisibilityPolicy` for discovery and by
    // `NodePairingHost` to gate `/pair/request`.
    public IReadOnlyList<string> AllowedDiscoveryRanges { get; init; } = [];

    public static NodeEndpointSettings Default { get; } = new();
}
