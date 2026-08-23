using Cockpit.Core.Secrets;

namespace Cockpit.Infrastructure.Configuration;

// The `Security` section: what the cockpit needs to know credentials are encrypted, and to turn the
// operator's password back into the key. None of it is secret — the verifier is a known string encrypted
// with the key, so a wrong password is told apart from a corrupt file rather than handed as garbage.
internal sealed class SecretProtectionEntry
{
    public bool Enabled { get; set; }

    // Base64. Fresh per installation, and again on every password change.
    public string Salt { get; set; } = string.Empty;

    // Named, not assumed, so a later build can move to Argon2id without stranding this file.
    public string Kdf { get; set; } = SecretKey.Pbkdf2Sha512;

    // Recorded, so raising the cost for new configs does not lock this one out.
    public int Iterations { get; set; } = SecretKey.DefaultIterations;

    // `VerifierPlaintext`, encrypted with the key. Decrypting it is how the unlock dialog checks a password.
    public string Verifier { get; set; } = string.Empty;

    // The known text behind `Verifier`, and the JSON path it is bound to.
    public const string VerifierPlaintext = "cockpit";

    public const string VerifierPath = "Security.Verifier";
}
