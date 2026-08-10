using System.Security.Cryptography;
using System.Text;

namespace Cockpit.Plugin.Depot.Secrets;

// Mirrors Cockpit.Core.Secrets.SecretProtector exactly (AC-607) — same wire format
// (`enc:v1:<base64(nonce|ciphertext|tag)>`), same AAD binding of the ciphertext to the path it sits at. Encrypts a
// project's sensitive AdditionalInfo values and the envelope's wrapped data keys before either ever reaches Depot.
public sealed class ProjectSecretProtector(byte[] key)
{
    public const string Prefix = "enc:v1:";

    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    public static bool IsProtected(string value) => value.StartsWith(Prefix, StringComparison.Ordinal);

    public string Protect(string path, string value)
    {
        if (IsProtected(value))
        {
            return value;
        }

        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var plaintext = Encoding.UTF8.GetBytes(value);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];

        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(path));

        var payload = new byte[NonceBytes + ciphertext.Length + TagBytes];
        nonce.CopyTo(payload, 0);
        ciphertext.CopyTo(payload, NonceBytes);
        tag.CopyTo(payload, NonceBytes + ciphertext.Length);

        return Prefix + Convert.ToBase64String(payload);
    }

    public string Unprotect(string path, string value)
    {
        if (!IsProtected(value))
        {
            return value;
        }

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(value[Prefix.Length..]);
        }
        catch (FormatException exception)
        {
            throw new ProjectSecretProtectionException($"The encrypted value at '{path}' is not readable.", exception);
        }

        if (payload.Length < NonceBytes + TagBytes)
        {
            throw new ProjectSecretProtectionException($"The encrypted value at '{path}' is truncated.");
        }

        var nonce = payload.AsSpan(0, NonceBytes);
        var ciphertext = payload.AsSpan(NonceBytes, payload.Length - NonceBytes - TagBytes);
        var tag = payload.AsSpan(payload.Length - TagBytes, TagBytes);
        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData(path));
        }
        catch (CryptographicException exception)
        {
            throw new ProjectSecretProtectionException($"The encrypted value at '{path}' could not be decrypted.", exception);
        }

        return Encoding.UTF8.GetString(plaintext);
    }

    private static byte[] AssociatedData(string path) => Encoding.UTF8.GetBytes(path);
}
