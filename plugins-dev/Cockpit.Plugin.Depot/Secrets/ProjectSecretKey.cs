using System.Security.Cryptography;

namespace Cockpit.Plugin.Depot.Secrets;

// Mirrors Cockpit.Core.Secrets.SecretKey exactly (AC-607) — the "plugin cannot reference Cockpit.Core"
// constraint (AC-244) applies here the same way it does to ProjectResourceSecretPathHeuristic. Kept in sync by
// ProjectSecretCryptoParityTests. Derives the AES-256 key that wraps a project's shared-field data key.
public static class ProjectSecretKey
{
    public const string Pbkdf2Sha512 = "pbkdf2-sha512";

    public const int DefaultIterations = 210_000;

    public const int SaltBytes = 16;

    private const int KeyBytes = 32; // AES-256

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltBytes);

    public static byte[] Derive(string password, byte[] salt, int iterations, string kdf = Pbkdf2Sha512)
    {
        if (!string.Equals(kdf, Pbkdf2Sha512, StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"This build does not know the key derivation function '{kdf}'.");
        }

        return Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, KeyBytes);
    }
}
