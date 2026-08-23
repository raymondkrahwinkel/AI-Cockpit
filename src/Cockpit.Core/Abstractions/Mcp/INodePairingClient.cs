using Cockpit.Core.Mcp;

namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// The controller half of the pairing handshake (AC-792): starts one against a hand-typed address, waits for the
/// node's operator, and collects the credential. Deliberately the only seam between a typed address and the pairing
/// protocol, so discovery (AC-793) can hand it a found address and reach exactly this code — criterion 6, two entrances one handshake.
/// </summary>
public interface INodePairingClient
{
    /// <summary>
    /// Asks the node at <paramref name="address"/> for a pairing and derives the comparison code from its actual
    /// certificate. Grants nothing: both operators still have to compare and confirm.
    /// </summary>
    /// <exception cref="NodePairingException">The node refused — already paired, or already pairing.</exception>
    Task<NodePairingHandshake> BeginAsync(string address, string controllerName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits for the node's operator to confirm and returns the credential and endpoint list. Pins the certificate
    /// seen in <see cref="BeginAsync"/> for the whole wait, so the answering machine is the one whose code was compared.
    /// </summary>
    /// <exception cref="NodePairingException">Expired, refused, already used, or the token was rejected.</exception>
    Task<NodePairingGrant> CompleteAsync(NodePairingHandshake handshake, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends the coupling from this side, so the node forgets it and invalidates the secret.
    /// </summary>
    Task UnpairAsync(string address, string sharedSecret, string certificateFingerprint, CancellationToken cancellationToken = default);
}
