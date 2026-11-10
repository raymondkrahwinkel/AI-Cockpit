using System.Net.Sockets;
using Cockpit.Plugin.RemoteFiles.Contracts;
using Cockpit.Plugins.Abstractions.Files;
using FluentFTP.Exceptions;
using Renci.SshNet.Common;
using Renci.SshNet.Security;

namespace Cockpit.Plugin.RemoteFiles.Tests;

// Category A, security: a login sent to an unpinned or changed key, a stored secret sent to another server, or a secret in an
// error text, leaks without a sound.
public sealed class RemoteTrustTests
{
    private const string Sentinel = "s3ntinel-9f2c";

    [Theory]
    [InlineData(null, "ssh-auth", "same")]
    [InlineData("same", "ftp-auth", "host")]
    [InlineData("other", "sftp-denied", "port")]
    [InlineData(null, "ssh-connection", "user")]
    [InlineData("same", "ftp-550", "kind")]
    [InlineData("other", "socket", "same")]
    [InlineData("same", "unknown", "host")]
    public void Only_the_pinned_key_lets_the_login_go_and_no_secret_leaves_for_another_server_or_in_an_error(string? pinned, string failure, string server)
    {
        var saved = new RemoteConnection("c1", "NAS", RemoteFilesChannel.Sftp, "nas.lan", 22, "raymond");
        var requested = server switch
        {
            "host" => saved with { Host = "attacker.example" },
            "port" => saved with { Port = 2222 },
            "user" => saved with { User = "other" },
            "kind" => saved with { Kind = RemoteFilesChannel.Ftp },
            _ => saved,
        };
        var sent = RemoteFilesPlugin.SecretsFor(saved, requested, new RemoteSecrets(Sentinel, Sentinel, Sentinel), new RemoteSecrets(Password: "typed"));
        Assert.Equal("typed", sent.Password);
        Assert.Equal(server == "same" ? Sentinel : null, sent.Key);
        Assert.Equal(server == "same" ? Sentinel : null, sent.Passphrase);
        Assert.DoesNotContain(Sentinel, sent.ToString());

        var presented = _HostKey(1);
        var pin = pinned switch
        {
            "same" => new RemotePin("ssh-ed25519", "SHA256:" + presented.FingerPrintSHA256, DateTimeOffset.UtcNow),
            "other" => new RemotePin("ssh-ed25519", "SHA256:" + _HostKey(2).FingerPrintSHA256, DateTimeOffset.UtcNow),
            _ => null,
        };
        var trust = new RemoteTrust(pin);

        trust.OnHostKey(null, presented);

        Assert.Equal(pinned == "same", presented.CanTrust);
        Assert.Equal(pinned != "same", trust.Refused);
        if (trust.Refused)
        {
            var refusal = trust.Refusal();
            Assert.Equal(FileSourceErrorKind.HostKey, refusal.Kind);
            Assert.Equal(pin?.Fingerprint, refusal.Trusted);
            Assert.Equal(pinned == "other" ? trust.Presented!.Fingerprint : null, refusal.Presented);
        }

        var message = $"{Sentinel}@{Sentinel}.example: password {Sentinel} for /home/{Sentinel}";
        Exception error = failure switch
        {
            "ssh-auth" => new SshAuthenticationException(message),
            "ftp-auth" => new FtpAuthenticationException("530", message),
            "sftp-denied" => new SftpPermissionDeniedException(message),
            "ssh-connection" => new SshConnectionException(message),
            "ftp-550" => new FtpCommandException("550", message),
            "socket" => new IOException(message, new SocketException()),
            _ => new InvalidOperationException(message),
        };
        var translated = RemoteTrust.Translate(error);
        Assert.DoesNotContain(Sentinel, translated.Message);
        Assert.Null(translated.InnerException);
        Assert.NotEqual(FileSourceErrorKind.Unsupported, translated.Kind);
    }

    private static HostKeyEventArgs _HostKey(byte seed) =>
        new(new KeyHostAlgorithm("ssh-ed25519", new ED25519Key(Enumerable.Repeat(seed, 32).ToArray())));
}
