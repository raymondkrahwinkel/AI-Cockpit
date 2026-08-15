using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Cockpit.Infrastructure.Mcp;

// A throwaway self-signed certificate for the node listener's TLS (AC-790). Regenerated once per app launch;
// nothing pins it to a fingerprint yet, so a restart changing it breaks nothing. Trust for this "first stone" is
// carried entirely by the shared secret — a connecting client has to be told out-of-band to accept this specific
// self-signed certificate. A pairing flow that lets a second cockpit remember and verify this certificate by
// fingerprint is a later, sibling ticket.
// ponytail: no certificate pinning/CA trust here — upgrade path is fingerprint pinning once that pairing sub exists.
internal static class NodeSelfSignedCertificate
{
    public static X509Certificate2 Create()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=cockpit-node", key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        // Re-imported via X509CertificateLoader so Kestrel's UseHttps can actually use the private key cross-platform
        // (CreateSelfSigned's own result is not reliably usable directly with UseHttps on Linux).
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), password: null);
    }
}
