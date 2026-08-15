namespace Cockpit.Core.Mcp;

// Who this node is currently paired with (AC-792) — the persistent half of the pairing handshake, stored beside
// the shared secret the handshake minted.
//
// AC-791 could get away without this: one node, one controller, one credential, so "revoke" was "rotate the
// secret". What it could not express is *whether* there is a coupling at all, or with whom — and both of those
// are needed here. A second pairing request has to be refused with the existing controller named (open point 3),
// and unpairing from the node's own screen has to be distinguishable from flipping the master switch off. Neither
// is answerable from a secret alone: a secret exists whether or not anybody ever used it.
public sealed record NodePairing
{
    // What the controller called itself when it asked. A label the operator recognises on the refusal, not an
    // identity — nothing authenticates it, and nothing keys on it.
    public required string ControllerName { get; init; }

    // The address the pairing request actually came from, as this machine saw it. Taken from the connection
    // rather than from the request body precisely because the body is the controller's word for it.
    public required string ControllerAddress { get; init; }

    public required DateTimeOffset PairedAtUtc { get; init; }
}

// A pairing this node has taken on and is still waiting for the operator to answer (AC-792) — what the node's own
// screen shows while it does. Absent the moment it is answered, expires or is claimed, so a screen showing this is
// always a screen with something left to press.
//
// Never persisted: a pairing that outlived the process it was started in would be a two-minute window that quietly
// became forever.
public sealed record NodePairingPending
{
    public required string PairingId { get; init; }

    public required string ControllerName { get; init; }

    public required string ControllerAddress { get; init; }

    // The six digits this side derived. The controller derives its own from what it saw on the wire; the operator
    // compares. See `NodePairingCode` for why neither side is told the other's.
    public required string Code { get; init; }

    public required DateTimeOffset ExpiresAtUtc { get; init; }
}
