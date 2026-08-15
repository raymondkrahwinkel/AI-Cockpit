using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cockpit.Core.Mcp;

// The wire shapes of the pairing handshake (AC-792), kept in one file because they are one protocol: a reader
// checking whether the node and the controller agree should not have to open five.
//
// The handshake, in the order it happens:
//
//   1. controller → node   POST /pair/request   `NodePairingRequest`
//      The node mints a pending pairing and answers `NodePairingOffer`. Nothing is granted yet.
//   2. both screens        derive `NodePairingCode` — the node from its own certificate, the controller from the
//                          one it saw — and the operator compares them.
//   3. node operator       confirms. Only now does a shared secret exist.
//   4. controller → node   POST /pair/claim     `NodePairingClaimRequest`
//      Pending until step 3, then once — `NodePairingGrant` — and never again.
//   5. either side         POST /pair/unpair (bearer: the shared secret) or the node's own screen.
//
// Deliberately plain HTTP+JSON rather than MCP tools: every one of these runs *before* the caller holds the
// credential the MCP listener demands, so there is no session, no tool set and no bearer to key on yet. The only
// authority in steps 1–4 is the claim token, which never leaves the TLS connection that asked for it.
public sealed record NodePairingRequest(string ControllerName);

// What the node hands back for a pairing it has taken on but not granted.
//
// `ClaimToken` is the whole reason an overheard code is not enough (criterion 4): it is 256 bits of randomness
// returned only in the response to the request that created this pairing, so the party that can later claim the
// secret is the party that opened that connection — not whoever read six digits off a screen. `Nonce` is public
// by design; it only has to be unpredictable enough that a code cannot be precomputed.
public sealed record NodePairingOffer(string PairingId, string ClaimToken, string Nonce, string NodeName, DateTimeOffset ExpiresAtUtc);

public sealed record NodePairingClaimRequest(string PairingId, string ClaimToken);

// The grant: the credential the node's MCP listeners will accept from now on, plus where they are. The endpoint
// list is here so the operator types one address (the pairing port) instead of one per mounted endpoint.
public sealed record NodePairingGrant(string SharedSecret, IReadOnlyList<NodeEndpointAddress> Endpoints);

// What the controller holds between steps 1 and 4 — not a wire shape, but the other half of the same protocol,
// so it lives beside it. `Code` is the number this side derived from the certificate it saw, which is the whole
// point of the comparison; `Fingerprint` is that certificate, kept so it can be pinned once the operator agrees.
public sealed record NodePairingHandshake(
    string Address,
    string PairingId,
    string ClaimToken,
    string NodeName,
    string Code,
    string Fingerprint,
    DateTimeOffset ExpiresAtUtc);

// A refusal a caller can classify, in the same shape and for the same reason as `McpAuthMiddleware`'s: a code to
// branch on and a sentence to show. See `NodePairingError` for the codes and what separates them.
public sealed record NodePairingProblem(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("error_description")] string ErrorDescription);

// The refusal codes. They exist as constants rather than prose because criteria 2 and 3 are precisely about two
// pairs of refusals staying *distinguishable* at the other end — expired from spent, and already-paired from a
// bad token — and a caller cannot branch on a sentence.
public static class NodePairingError
{
    // No such pairing, or the claim token does not match it. Deliberately one code for both, so the answer is
    // not an oracle for "this pairing id exists".
    public const string InvalidToken = "invalid_token";

    // The pairing exists and the token is right, but the node operator has not confirmed yet. Not a failure —
    // the controller is meant to keep asking until this stops.
    public const string Pending = "pending";

    // The two-minute window closed before it was claimed.
    public const string Expired = "expired";

    // It was claimed once already. Separate from `Expired` on purpose (criterion 2): "you were too slow" and
    // "somebody already used this" are different events, and only the second one is worth alarm.
    public const string AlreadyUsed = "already_used";

    // The node operator pressed Refuse.
    public const string Refused = "refused";

    // This node already has a controller (open point 3). The description names it, so the operator can tell this
    // from a credential problem without guessing.
    public const string AlreadyPaired = "already_paired";

    // Another pairing is pending. A second request must not be allowed to quietly replace the one the operator
    // is looking at — that would make cancelling somebody else's pairing a thing an unauthenticated caller can do.
    public const string PairingInProgress = "pairing_in_progress";
}

// A refusal raised in-process, carrying the same problem the wire would have. The broker throws these so the HTTP
// host has one thing to translate and the node's own UI has one thing to show — the alternative is a result type
// whose every caller re-derives the status code from the code string.
public sealed class NodePairingException(NodePairingProblem problem) : Exception(problem.ErrorDescription)
{
    public NodePairingProblem Problem { get; } = problem;

    public string Error => Problem.Error;

    public static NodePairingException For(string error, string description) => new(new NodePairingProblem(error, description));
}

public static class NodePairingJson
{
    // One options instance for both ends. camelCase because that is what the rest of `cockpit.json` and every
    // other JSON surface here uses; case-insensitive reading so a hand-written probe against this endpoint is
    // not defeated by capitalisation.
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}
