namespace Cockpit.Core.Mcp;

// The network-node master switch (AC-790): off by default, so a Cockpit nobody meant as a node stays on
// loopback only. Turning it on adds a second, HTTPS listener next to each mounted endpoint's existing loopback
// one, guarded by SharedSecret instead of this run's ephemeral McpAuthKey — the credential a second Cockpit
// types in by hand when it adds this node as a plain HTTP MCP server (AC-354), so it has to survive a restart.
// Takes effect on the next launch, same as RenderingSettingsEntry — no dynamic Kestrel listener reconfiguration.
public sealed record NodeEndpointSettings
{
    public bool Enabled { get; init; }
    public string SharedSecret { get; init; } = "";

    // Who this node is paired with (AC-792), or null while nobody is. Null with a non-empty `SharedSecret` is a
    // real and supported state, not a broken one: that is a node whose operator turned the switch on and read the
    // secret off the Security tab by hand, the way AC-790 shipped it. Pairing is a second way to arrive at the
    // same credential, not a replacement for the first.
    public NodePairing? Pairing { get; init; }

    // CIDR ranges allowed to see this node from outside its own local network (AC-793), e.g. "203.0.113.0/24".
    // Empty by default: the node's own subnet is always visible, and nothing past it until the operator opts in —
    // `INodeVisibilityPolicy` is what actually reads this, both when answering a discovery query and when
    // `NodePairingHost` decides whether to accept a `/pair/request` at all.
    public IReadOnlyList<string> AllowedDiscoveryRanges { get; init; } = [];

    public static NodeEndpointSettings Default { get; } = new();
}
