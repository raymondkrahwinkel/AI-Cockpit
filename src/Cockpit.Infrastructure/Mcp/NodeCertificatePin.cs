using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ModelContextProtocol.Client;
using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Mcp;

// The controller's side of the node's identity (AC-792): trust exactly one self-signed certificate, the one
// remembered at pairing time, and nothing else.
//
// A node signs its own certificate, so the machine's trust store has no opinion about it — which leaves two
// options. Turn validation off and accept whatever answers on that address, which is what "TLS over the LAN"
// degenerates into and is why AC-790 called its own transport encryption without an identity. Or pin: decide once,
// during a handshake a human is watching, and refuse every later deviation. Pinning is the only one of the two
// that makes the shared secret worth guarding, because a machine in the middle that can present its own
// certificate reads the bearer token on the very first call.
//
// Two modes, because pairing and use want opposite things from the same callback:
//
//   * `Observe` — no pin yet; accept the certificate but record its fingerprint, so the pairing client can derive
//     the comparison code from what it *actually saw* (see `NodePairingCode`) and pin it afterwards. Acceptance
//     here is not trust: nothing is granted until the operator compares two numbers.
//   * `Require` — a pin exists; anything else is refused with a reason that names both fingerprints.
internal static class NodeCertificatePin
{
    // Accepts any server certificate and hands its fingerprint back through `observed`. Only for the pairing
    // handshake, where there is nothing to compare against yet and a human closes the gap.
    public static HttpMessageHandler Observe(Action<string> observed) =>
        new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is not null)
                {
                    observed(FingerprintOf(certificate));
                }

                return true;
            },
        };

    // Accepts only the pinned certificate. Throws rather than returning false so the caller gets a reason it can
    // tell from every other transport failure — see `NodeCertificatePinMismatchException`.
    public static HttpMessageHandler Require(string expectedFingerprint)
    {
        var expected = NodePairingCode.Normalize(expectedFingerprint);

        return new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                var presented = certificate is null ? null : FingerprintOf(certificate);
                if (presented is not null && CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.ASCII.GetBytes(presented),
                        System.Text.Encoding.ASCII.GetBytes(expected)))
                {
                    return true;
                }

                throw new NodeCertificatePinMismatchException(expected, presented);
            },
        };
    }

    // SHA-256 over the DER encoding, uppercase hex — the same string `NodeSelfSignedCertificate.Fingerprint`
    // produces, so the node and the controller are always comparing like with like.
    public static string FingerprintOf(X509Certificate certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    // The HTTP transport for `server`, pinned when it is a paired node and left exactly as it was for everything
    // else. One helper rather than the same conditional at both call sites (`McpToolProvider` and `McpToolProbe`):
    // a rule about which certificate is acceptable that is stated twice is the one that drifts, and the drift here
    // would be a probe that silently trusts what the session refuses.
    //
    // `ownsHttpClient: true` — the pinned client exists for this transport alone, so its lifetime is the
    // transport's; the parameter defaults to false, which would leak one handler per connection.
    public static HttpClientTransport TransportFor(McpServerConfig server, HttpClientTransportOptions options) =>
        string.IsNullOrEmpty(server.PinnedCertificateFingerprint)
            ? new HttpClientTransport(options)
            : new HttpClientTransport(options, new HttpClient(Require(server.PinnedCertificateFingerprint)), loggerFactory: null, ownsHttpClient: true);
}
