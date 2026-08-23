using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ModelContextProtocol.Client;
using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Mcp;

// AC-792: trust exactly one self-signed node certificate, the one remembered at pairing time — pinning (decide
// once during a human-watched handshake, refuse every deviation) is the only option that makes the shared
// secret worth guarding. Observe records the fingerprint (no pin yet); Require refuses anything but the pin.
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

    // The HTTP transport for `server`, pinned when paired and unchanged otherwise — one helper rather than the
    // same conditional at both call sites, since a duplicated rule is the one that drifts. ownsHttpClient: true
    // since the pinned client exists for this transport alone; the default false would leak a handler per call.
    public static HttpClientTransport TransportFor(McpServerConfig server, HttpClientTransportOptions options) =>
        string.IsNullOrEmpty(server.PinnedCertificateFingerprint)
            ? new HttpClientTransport(options)
            : new HttpClientTransport(options, new HttpClient(Require(server.PinnedCertificateFingerprint)), loggerFactory: null, ownsHttpClient: true);
}
