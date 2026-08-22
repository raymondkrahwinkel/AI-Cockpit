using Cockpit.Core.Mcp;

namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// The node half of the pairing handshake (AC-792): it decides, remembers and refuses. The HTTP surface in front
/// of it only translates, and the Security tab only shows what is here — so the whole of "may this pairing happen"
/// is testable without a socket.
/// </summary>
public interface INodePairingBroker
{
    /// <summary>Who this node is paired with, or null.</summary>
    NodePairing? Pairing { get; }

    /// <summary>The pairing waiting for the operator, or null. Expired ones read as null.</summary>
    NodePairingPending? Pending { get; }

    /// <summary>Raised when <see cref="Pending"/> or <see cref="Pairing"/> changes, so the Security tab can follow along.</summary>
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

    /// <summary>The operator confirmed the code matches. Mints the shared secret and records the pairing.</summary>
    Task ConfirmAsync(string pairingId, CancellationToken cancellationToken = default);

    /// <summary>The operator refused, or closed the prompt.</summary>
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
    /// there is no pairing at all, and false for a profile the operator never ticked — a fresh or just-revoked
    /// pairing grants nothing, there is no implicit "everything until narrowed".
    /// </summary>
    bool IsProfileAllowed(string profileLabel);

    /// <summary>Same as <see cref="IsProfileAllowed"/>, for a project by <see cref="Cockpit.Core.Projects.Project.Id"/>.</summary>
    bool IsProjectAllowed(string projectId);

    /// <summary>
    /// Replaces which profiles and projects the current pairing may use. A no-op while unpaired. Takes effect on
    /// the running listener at once, the same as <see cref="ConfirmAsync"/> and <see cref="UnpairAsync"/>: a scope
    /// narrowed here must stop covering the next call, not the next restart.
    /// </summary>
    Task SetScopeAsync(IReadOnlyList<string> allowedProfileLabels, IReadOnlyList<string> allowedProjectIds, CancellationToken cancellationToken = default);
}
