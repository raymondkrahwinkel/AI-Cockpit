namespace Cockpit.Core.Mcp;

// AC-792: who this node is currently paired with — the persistent half of the handshake, stored beside the
// shared secret it minted. Unlike AC-791's secret-only model, this can express whether a coupling exists and
// with whom, needed to refuse a second pairing request by name and to distinguish unpairing from a master-switch flip.
public sealed record NodePairing
{
    // What the controller called itself when it asked. A label the operator recognises on the refusal, not an
    // identity — nothing authenticates it, and nothing keys on it.
    public required string ControllerName { get; init; }

    // The address the pairing request actually came from, as this machine saw it. Taken from the connection
    // rather than from the request body precisely because the body is the controller's word for it.
    public required string ControllerAddress { get; init; }

    public required DateTimeOffset PairedAtUtc { get; init; }

    // AC-794: profiles/projects this pairing may use, empty by default until the operator opts in. No wildcard
    // is possible by construction, so nothing can silently widen an existing pairing. Read by
    // `NodePairingBroker.IsProfileAllowed`/`IsProjectAllowed`.
    public IReadOnlyList<string> AllowedProfileLabels { get; init; } = [];
    public IReadOnlyList<string> AllowedProjectIds { get; init; } = [];
}

// AC-792: a pairing this node has taken on and is still waiting for the operator to answer. Never persisted —
// a pairing outliving its process would turn a two-minute window into forever.
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
