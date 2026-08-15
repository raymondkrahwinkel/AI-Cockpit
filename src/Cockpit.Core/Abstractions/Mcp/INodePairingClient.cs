using Cockpit.Core.Mcp;

namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// The controller half of the pairing handshake (AC-792): starts one against a hand-typed address, waits for the
/// node's operator, and collects the credential.
/// </summary>
/// <remarks>
/// Deliberately the only thing between the address the operator types and the pairing protocol, so that discovery
/// (AC-793) can hand it an address it found instead of one that was typed and reach exactly this code. That is
/// criterion 6 — two entrances, one handshake — expressed as a seam rather than as a promise.
/// </remarks>
public interface INodePairingClient
{
    /// <summary>
    /// Asks the node at <paramref name="address"/> for a pairing and derives the comparison code from the
    /// certificate that address actually presented. Grants nothing: the operator still has to compare the code and
    /// the node's operator still has to confirm.
    /// </summary>
    /// <exception cref="NodePairingException">The node refused — already paired, or already pairing.</exception>
    Task<NodePairingHandshake> BeginAsync(string address, string controllerName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits for the node's operator to confirm and returns the credential and endpoint list. Pins the
    /// certificate seen in <see cref="BeginAsync"/> for the whole wait, so the machine that answers here is the
    /// machine whose code was compared.
    /// </summary>
    /// <exception cref="NodePairingException">Expired, refused, already used, or the token was rejected.</exception>
    Task<NodePairingGrant> CompleteAsync(NodePairingHandshake handshake, CancellationToken cancellationToken = default);

    /// <summary>Ends the coupling from this side, so the node forgets it and invalidates the secret.</summary>
    Task UnpairAsync(string address, string sharedSecret, string certificateFingerprint, CancellationToken cancellationToken = default);
}
