using Cockpit.Core.Secrets;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// The <c>Security</c> section of <c>cockpit.json</c>: what the cockpit needs in order to know that its
/// credentials are encrypted, and to turn the operator's password back into the key.
/// <para>
/// None of it is a secret. The salt is public by design (it exists so the same password on two machines yields
/// different keys, not to be hidden), the iteration count is a cost, and the verifier is a known string
/// encrypted with the key — which is how a wrong password is told apart from a corrupt file, instead of the
/// operator being handed garbage and left to guess which of the two happened.
/// </para>
/// </summary>
internal sealed class SecretProtectionEntry
{
    public bool Enabled { get; set; }

    /// <summary>Base64. Fresh per installation, and again on every password change.</summary>
    public string Salt { get; set; } = string.Empty;

    /// <summary>Named, not assumed, so a later build can move to Argon2id without stranding this file.</summary>
    public string Kdf { get; set; } = SecretKey.Pbkdf2Sha512;

    /// <summary>Recorded, so raising the cost for new configs does not lock this one out.</summary>
    public int Iterations { get; set; } = SecretKey.DefaultIterations;

    /// <summary><see cref="VerifierPlaintext"/>, encrypted with the key. Decrypting it is how the unlock dialog checks a password.</summary>
    public string Verifier { get; set; } = string.Empty;

    /// <summary>The known text behind <see cref="Verifier"/>, and the JSON path it is bound to.</summary>
    public const string VerifierPlaintext = "cockpit";

    public const string VerifierPath = "Security.Verifier";
}
