using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Cockpit.Plugin.Proxmox.Engine;

// Reads the certificate a Proxmox host presents, the way an SSH client shows a host key on first connect (AC-1038).
// Proxmox is self-signed by default, so trust here is never automatic: this only reads and formats the fingerprint
// for the operator to look at in the settings UI; nothing is trusted until they explicitly confirm it, and the
// probe connection carries no request beyond the TLS handshake itself.
internal static class ProxmoxCertificateProbe
{
    public static async Task<(string? Fingerprint, string? Error)> FetchFingerprintAsync(string host, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(host, port, cancellationToken);

            // Deliberately accept whatever is presented here — the point of this probe is to read it and show it,
            // not to validate it. No data beyond the handshake is exchanged, and nothing is persisted as trusted
            // until the operator explicitly confirms the fingerprint shown.
            using var sslStream = new SslStream(tcpClient.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
            await sslStream.AuthenticateAsClientAsync(host);

            if (sslStream.RemoteCertificate is null)
            {
                return (null, "The server did not present a certificate.");
            }

            using var certificate = new X509Certificate2(sslStream.RemoteCertificate);
            return (Fingerprint(certificate), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, $"Could not reach {host}:{port} to read its certificate ({ex.GetType().Name}). Check the host and port.");
        }
    }

    // The SHA-256 fingerprint, lower-case colon-separated hex — the same format an SSH client shows a host key in.
    public static string Fingerprint(X509Certificate2 certificate) =>
        string.Join(":", Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256)).Chunk(2).Select(chunk => new string(chunk)));
}
