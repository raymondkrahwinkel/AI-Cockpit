using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Cockpit.Core.Abstractions;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Mcp;

// AC-790/AC-792: this machine's identity as a node — a self-signed TLS certificate kept on disk so it's the
// same one tomorrow, letting the controller pin its fingerprint (NodeCertificatePin) across restarts.
// Regenerated when missing/unreadable/expired — existing controllers will then refuse it, the honest outcome.
internal sealed class NodeSelfSignedCertificate : ISingletonService, IDisposable
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private X509Certificate2? _certificate;

    public NodeSelfSignedCertificate()
        : this(CockpitConfigPath.NodeCertificate)
    {
    }

    // Test seam: point the certificate at an arbitrary file.
    internal NodeSelfSignedCertificate(string path)
    {
        _path = path;
    }

    public X509Certificate2 Value
    {
        get
        {
            lock (_gate)
            {
                return _certificate ??= _LoadOrCreate();
            }
        }
    }

    // SHA-256 of the DER encoding, uppercase hex — what a controller pins and what feeds `NodePairingCode`.
    public string Fingerprint => Value.GetCertHashString(HashAlgorithmName.SHA256);

    private X509Certificate2 _LoadOrCreate()
    {
        if (_TryLoad() is { } existing)
        {
            return existing;
        }

        // The PKCS#12 bytes are produced before anything is re-imported — exporting the loaded certificate
        // instead is a Windows-only trap (X509CertificateLoader's keyset isn't marked exportable there).
        var bytes = _CreatePkcs12();

        try
        {
            using var stream = CockpitConfigPath.CreatePrivateFile(_path);
            stream.Write(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // A node that cannot write its certificate still works this run — it just cannot be pinned across a
            // restart. Failing to start the listener over it would cost more than it saves.
        }

        return X509CertificateLoader.LoadPkcs12(bytes, password: null);
    }

    private X509Certificate2? _TryLoad()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            var certificate = X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(_path), password: null);

            // A certificate outside its validity window is refused by every TLS stack, so keeping it would leave
            // the listener unusable with no way out but deleting a file by hand.
            if (DateTimeOffset.UtcNow > certificate.NotAfter || DateTimeOffset.UtcNow < certificate.NotBefore)
            {
                certificate.Dispose();
                return null;
            }

            return certificate;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // A fresh node identity as PKCS#12 bytes. Kept as bytes rather than a certificate because those bytes are both
    // what goes on disk and what gets re-imported — re-importing is what makes the private key usable by Kestrel's
    // `UseHttps` cross-platform, which `CreateSelfSigned`'s own result is not reliably.
    private static byte[] _CreatePkcs12()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=cockpit-node", key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));
        return certificate.Export(X509ContentType.Pfx);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _certificate?.Dispose();
            _certificate = null;
        }
    }
}
