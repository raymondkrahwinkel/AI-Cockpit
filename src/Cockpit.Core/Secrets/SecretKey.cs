using System.Security.Cryptography;

namespace Cockpit.Core.Secrets;

/// <summary>
/// The key the cockpit's credentials are encrypted with, derived from the operator's password.
/// <para>
/// The password is the only source: no key file, no keyring entry, nothing on disk. That is what the promise
/// rests on — someone who takes the config file, a backup, or the whole laptop has ciphertext and no key. It is
/// also what makes a forgotten password final, which is stated plainly where the operator sets one.
/// </para>
/// <para>
/// PBKDF2-HMAC-SHA512 rather than Argon2id: it is in the framework, and a NuGet dependency for the crypto that
/// guards the crypto is a trade we did not want to make for v1. Argon2id resists GPU cracking better, and the
/// KDF is named in the settings precisely so it can be swapped without stranding an existing config.
/// </para>
/// </summary>
public static class SecretKey
{
    public const string Pbkdf2Sha512 = "pbkdf2-sha512";

    /// <summary>OWASP's floor for PBKDF2-HMAC-SHA512 at the time of writing. Recorded per config, so raising it later does not strand an existing one.</summary>
    public const int DefaultIterations = 210_000;

    public const int SaltBytes = 16;

    private const int KeyBytes = 32; // AES-256

    /// <summary>A fresh random salt. One per installation, so the same password on two machines yields different keys.</summary>
    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltBytes);

    /// <summary>Derives the AES-256 key from the password.</summary>
    public static byte[] Derive(string password, byte[] salt, int iterations, string kdf = Pbkdf2Sha512)
    {
        if (!string.Equals(kdf, Pbkdf2Sha512, StringComparison.OrdinalIgnoreCase))
        {
            // A config written by a future version with a KDF this build does not know. Refusing beats deriving
            // the wrong key and reporting a wrong password for something the operator typed correctly.
            throw new NotSupportedException($"This build does not know the key derivation function '{kdf}'.");
        }

        return Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, KeyBytes);
    }
}
