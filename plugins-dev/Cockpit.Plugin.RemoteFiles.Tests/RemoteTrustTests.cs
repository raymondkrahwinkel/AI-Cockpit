using System.Net.Sockets;
using Cockpit.Plugin.RemoteFiles.Contracts;
using Cockpit.Plugins.Abstractions.Files;
using FluentFTP.Exceptions;
using Renci.SshNet.Common;
using Renci.SshNet.Security;

namespace Cockpit.Plugin.RemoteFiles.Tests;

// Category A, security: a login sent to an unpinned or changed key, or a secret in an error text, leaks without a sound.
public sealed class RemoteTrustTests
{
    private const string Sentinel = "s3ntinel-9f2c";

    [Theory]
    [InlineData(null, "ssh-auth")]
    [InlineData("same", "ftp-auth")]
    [InlineData("other", "sftp-denied")]
    [InlineData(null, "ssh-connection")]
    [InlineData("same", "ftp-550")]
    [InlineData("other", "socket")]
    [InlineData("same", "unknown")]
    public void Only_the_pinned_key_lets_the_login_go_and_no_error_carries_a_secret(string? pinned, string failure)
    {
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
        if (pinned == "other")
        {
            var refusal = trust.Refusal().Message;
            Assert.Contains(pin!.Fingerprint, refusal);
            Assert.Contains(trust.Presented!.Fingerprint, refusal);
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
