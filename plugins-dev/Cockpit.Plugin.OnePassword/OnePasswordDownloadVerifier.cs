using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace Cockpit.Plugin.OnePassword;

internal static class OnePasswordDownloadVerifier
{
    private const string Fingerprint = "3FEF9748469ADBE15DA7CA80AC2D62742012EA22";

    public static async Task VerifyAsync(byte[] archiveBytes, string executablePath, CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(new MemoryStream(archiveBytes), ZipArchiveMode.Read);
        var executable = OperatingSystem.IsWindows() ? "op.exe" : "op";
        var signatureEntry = archive.GetEntry(executable + ".sig")
            ?? throw new InvalidOperationException("The 1Password download has no official signature and was refused.");
        if (signatureEntry.Length > 65536)
        {
            throw new InvalidOperationException("The 1Password signature exceeds the size limit and was refused.");
        }
        using var signatureStream = signatureEntry.Open();
        using var signatureBytes = new MemoryStream();
        await signatureStream.CopyToAsync(signatureBytes, cancellationToken).ConfigureAwait(false);
        signatureBytes.Position = 0;
        using var decoder = PgpUtilities.GetDecoderStream(signatureBytes);
        var signatures = new PgpObjectFactory(decoder).NextPgpObject() as PgpSignatureList;
        if (signatures is null || signatures.Count != 1)
        {
            throw new InvalidOperationException("The 1Password download has an invalid signature and was refused.");
        }

        using var keyStream = typeof(OnePasswordDownloadVerifier).Assembly.GetManifestResourceStream("Cockpit.Plugin.OnePassword.SigningKey.asc")
            ?? throw new InvalidOperationException("The 1Password signing key is unavailable.");
        using var keyDecoder = PgpUtilities.GetDecoderStream(keyStream);
        var keyRing = new PgpPublicKeyRing(keyDecoder);
        if (Convert.ToHexString(keyRing.GetPublicKey().GetFingerprint()) != Fingerprint)
        {
            throw new InvalidOperationException("The 1Password signing key fingerprint does not match.");
        }

        var signature = signatures[0];
        var key = keyRing.GetPublicKey(signature.KeyId)
            ?? throw new InvalidOperationException("The download was not signed by the 1Password signing key.");
        signature.InitVerify(key);
        await using (var binary = File.OpenRead(executablePath))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await binary.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                signature.Update(buffer, 0, read);
            }
        }

        if (!signature.Verify())
        {
            throw new InvalidOperationException("The 1Password download did not match its official signature and was refused.");
        }

        if (OperatingSystem.IsWindows())
        {
            await _VerifyAuthenticodeAsync(executablePath, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task _VerifyAuthenticodeAsync(string path, CancellationToken cancellationToken)
    {
        var script = "$s = Get-AuthenticodeSignature -LiteralPath '" + path.Replace("'", "''") + "'; "
            + "if ($s.Status -ne 'Valid' -or $s.SignerCertificate.GetNameInfo('SimpleName', $false) -ne 'Agilebits' "
            + "-or $s.SignerCertificate.Issuer -notmatch 'O=Microsoft Corporation' "
            + "-or '1.3.6.1.4.1.311.97.661420558.769123285.207353056.500447802' -notin $s.SignerCertificate.EnhancedKeyUsageList.ObjectId) { exit 1 }";
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not verify 1Password Authenticode.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("The 1Password Authenticode signature is invalid or has an unexpected publisher; the download was refused.");
        }
    }
}
