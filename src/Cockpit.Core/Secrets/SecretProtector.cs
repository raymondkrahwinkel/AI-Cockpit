using System.Security.Cryptography;
using System.Text;

namespace Cockpit.Core.Secrets;

/// <summary>
/// Encrypts and decrypts one credential value.
/// <para>
/// AES-256-GCM: authenticated, so a tampered value fails loudly instead of decrypting to plausible nonsense.
/// The field's JSON path is the associated data, which binds the ciphertext to where it sits — a token cannot
/// be lifted out of one field and pasted into another to be decrypted there.
/// </para>
/// <para>
/// Stored as <c>enc:v1:&lt;base64(nonce|ciphertext|tag)&gt;</c>. The prefix is what lets the cockpit tell an
/// encrypted value from a plain one without being told, which is what makes a half-migrated file readable
/// rather than a puzzle — and the version is there so a later format has a way in.
/// </para>
/// </summary>
public sealed class SecretProtector(byte[] key) : ISecretProtector
{
    public const string Prefix = "enc:v1:";

    private const int NonceBytes = 12; // AesGcm.NonceByteSizes.MaxSize
    private const int TagBytes = 16; // AesGcm.TagByteSizes.MaxSize

    /// <summary>Whether <paramref name="value"/> is already encrypted. Used to leave a half-migrated file alone rather than encrypt ciphertext twice.</summary>
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
            throw new SecretProtectionException($"The encrypted value at '{path}' is not readable.", exception);
        }

        if (payload.Length < NonceBytes + TagBytes)
        {
            throw new SecretProtectionException($"The encrypted value at '{path}' is truncated.");
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
            // The wrong key, or a value that was altered — GCM cannot tell you which, and neither can we. Both
            // mean: do not hand back a value, and do not touch the file.
            throw new SecretProtectionException($"The encrypted value at '{path}' could not be decrypted.", exception);
        }

        return Encoding.UTF8.GetString(plaintext);
    }

    private static byte[] AssociatedData(string path) => Encoding.UTF8.GetBytes(path);
}
