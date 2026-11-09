using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Cockpit.Plugin.RemoteFiles.Contracts;
using Cockpit.Plugins.Abstractions.Files;
using FluentFTP;
using FluentFTP.Client.BaseClient;
using FluentFTP.Exceptions;
using Renci.SshNet.Common;

namespace Cockpit.Plugin.RemoteFiles;

internal enum PinVerdict { Unknown, Trusted, Changed }

// Decides before any login leaves: only a key or certificate equal to the pinned one lets the connection go on.
internal sealed class RemoteTrust(RemotePin? pin)
{
    public RemotePresented? Presented { get; private set; }

    public PinVerdict? Verdict { get; private set; }

    public void OnHostKey(object? sender, HostKeyEventArgs e)
    {
        // SSH.NET trusts every key unless told otherwise.
        e.CanTrust = false;
        Presented = new RemotePresented($"{e.HostKeyName} ({e.KeyLength}-bit)", "SHA256:" + e.FingerPrintSHA256);
        Verdict = _Decide(Presented.Fingerprint);
        e.CanTrust = Verdict == PinVerdict.Trusted;
    }

    // A certificate the system trusts needs no pin; any other only passes when it is the pinned one.
    public void OnCertificate(BaseFtpClient sender, FtpSslValidationEventArgs e)
    {
        e.Accept = false;
        if (e.PolicyErrors == SslPolicyErrors.None)
        {
            e.Accept = true;
            return;
        }
        using var certificate = new X509Certificate2(e.Certificate);
        Presented = new RemotePresented("certificate", string.Join(':', Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)).Chunk(2).Select(pair => new string(pair))),
            certificate.Subject, certificate.Issuer, certificate.NotAfter);
        Verdict = _Decide(Presented.Fingerprint);
        e.Accept = Verdict == PinVerdict.Trusted;
    }

    private PinVerdict _Decide(string presented) =>
        pin is null ? PinVerdict.Unknown
        : string.Equals(pin.Fingerprint, presented, StringComparison.Ordinal) ? PinVerdict.Trusted
        : PinVerdict.Changed;

    public bool Refused => Verdict is PinVerdict.Unknown or PinVerdict.Changed;

    public FileSourceException Refusal() => Verdict == PinVerdict.Changed
        ? new FileSourceException(FileSourceErrorKind.HostKey,
            "The server presented a different key than the one you trusted. Cockpit did not connect and sent nothing. Forget the old key in this connection's settings and test it again.")
        {
            Trusted = pin!.Fingerprint,
            Presented = Presented!.Fingerprint,
        }
        : new FileSourceException(FileSourceErrorKind.HostKey,
            "Test this connection in Options › Plugins › Remote files and trust its key first. Nothing is sent until you do.");

    // Library messages can carry the host, the user name or a path, so only these fixed sentences ever leave.
    public static FileSourceException Translate(Exception error) => error switch
    {
        FileSourceException known => known,
        SshAuthenticationException or FtpAuthenticationException =>
            new(FileSourceErrorKind.SignIn, "The server refused the sign-in. Check the user name and the password or key."),
        SftpPathNotFoundException or FtpMissingObjectException or FileNotFoundException or DirectoryNotFoundException =>
            new(FileSourceErrorKind.NotFound, "This item does not exist on the server."),
        SftpPermissionDeniedException or UnauthorizedAccessException =>
            new(FileSourceErrorKind.Forbidden, "The server refused access to this item."),
        FtpCommandException { CompletionCode: "550" } =>
            new(FileSourceErrorKind.NotFound, "This item does not exist on the server, or the server refused access to it."),
        FtpSecurityNotAvailableException =>
            new(FileSourceErrorKind.Forbidden, "The server does not offer encryption, so Cockpit sent nothing. Choose FTP only if you accept that."),
        FtpInvalidCertificateException or AuthenticationException =>
            new(FileSourceErrorKind.Forbidden, "The server's certificate could not be checked, so Cockpit sent nothing."),
        SocketException or SshConnectionException or SshOperationTimeoutException or TimeoutException or IOException or ProxyException =>
            new(FileSourceErrorKind.Unavailable, "The server could not be reached, or it closed the connection."),
        _ => new(FileSourceErrorKind.Unavailable, "The remote file action failed."),
    };
}
