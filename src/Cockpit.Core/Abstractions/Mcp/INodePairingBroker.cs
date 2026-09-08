using Cockpit.Core.Mcp;

namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// The node half of the pairing handshake (AC-792): it decides, remembers and refuses. The HTTP surface in front
/// of it only translates, and the Security tab only shows what is here — so the whole of "may this pairing happen"
/// is testable without a socket.
/// </summary>
public interface INodePairingBroker
{
    /// <summary>
    /// Who this node is paired with, or null.
    /// </summary>
    NodePairing? Pairing { get; }

    /// <summary>
    /// The pairing waiting for the operator, or null. Expired ones read as null.
    /// </summary>
    NodePairingPending? Pending { get; }

    /// <summary>
    /// Raised when <see cref="Pending"/> or <see cref="Pairing"/> changes, so the Security tab can follow along.
    /// </summary>
    event EventHandler? Changed;

    /// <summary>
    /// Reads the persisted pairing in, so <see cref="Pairing"/> answers for a coupling made before this launch.
    /// Idempotent; every other method here calls it first, and a view that only reads has to call it itself.
    /// </summary>
    Task EnsureLoadedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes on a pairing request. Refuses with <see cref="NodePairingError.AlreadyPaired"/> when already
    /// controlled, and with <see cref="NodePairingError.PairingInProgress"/> while another request still waits.
    /// </summary>
    /// <exception cref="NodePairingException">The request is refused; <c>Problem</c> says why.</exception>
    Task<NodePairingOffer> RequestAsync(string controllerName, string controllerAddress, CancellationToken cancellationToken = default);

    /// <summary>
    /// The operator confirmed the code matches. Mints the shared secret and records the pairing.
    /// </summary>
    Task ConfirmAsync(string pairingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The operator refused, or closed the prompt.
    /// </summary>
    void Refuse(string pairingId);

    /// <summary>
    /// The controller comes back for the credential. Succeeds once, for the holder of the claim token, after the
    /// operator confirmed.
    /// </summary>
    /// <exception cref="NodePairingException">Pending, expired, already used, refused, or the wrong token.</exception>
    Task<NodePairingGrant> ClaimAsync(string pairingId, string claimToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends the coupling: forgets the controller and invalidates the shared secret it was granted, so the
    /// credential AC-790 hands to the MCP listeners stops being accepted. Reached from this node's own screen or
    /// from the controller over <c>/pair/unpair</c> — both land here.
    /// </summary>
    Task UnpairAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// True if the current pairing may use the profile named <paramref name="profileLabel"/> (AC-794). False when
    /// there is no pairing at all: an unpaired or just-revoked node grants nothing. Within a pairing, true for a
    /// profile the operator ticked, and true for every profile while <c>NodePairing.AllowAllProfiles</c> is set —
    /// the stand a fresh pairing starts on (AC-1292), so a profile made later is reachable without re-ticking.
    /// </summary>
    bool IsProfileAllowed(string profileLabel);

    /// <summary>
    /// Same as <see cref="IsProfileAllowed"/>, for a project by <see cref="Cockpit.Core.Projects.Project.Id"/>.
    /// </summary>
    bool IsProjectAllowed(string projectId);

    /// <summary>
    /// Replaces which profiles and projects the current pairing may use. A no-op while unpaired. Takes effect on
    /// the running listener at once, the same as <see cref="ConfirmAsync"/> and <see cref="UnpairAsync"/>: a scope
    /// narrowed here must stop covering the next call, not the next restart. The id lists are stored even while
    /// the matching "all" flag is on, so turning that flag off restores the operator's own selection (AC-1292).
    /// </summary>
    Task SetScopeAsync(
        IReadOnlyList<string> allowedProfileLabels,
        IReadOnlyList<string> allowedProjectIds,
        bool allowAllProfiles,
        bool allowAllProjects,
        CancellationToken cancellationToken = default);
}
