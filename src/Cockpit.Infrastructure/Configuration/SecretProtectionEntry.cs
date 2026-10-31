using System.Text.Json.Serialization;
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

    // AC-1879: the key, wrapped by Windows Hello; null while Hello is off. A password change drops it.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HelloEntry? Hello { get; set; }

    // The known text behind `Verifier`, and the JSON path it is bound to.
    public const string VerifierPlaintext = "cockpit";

    public const string VerifierPath = "Security.Verifier";
}

// AC-1879: none of it is secret. `WrappedKey` is the vault key under a key derived from the OS key's signature over
// `Challenge`; without the TPM-held key and the Hello gesture it is worthless, and the password path stays as it was.
internal sealed class HelloEntry
{
    public string CredentialName { get; set; } = string.Empty;

    // Base64, 32 random bytes.
    public string Challenge { get; set; } = string.Empty;

    public string WrappedKey { get; set; } = string.Empty;

    public const string WrappedKeyPath = "Security.Hello.WrappedKey";
}
