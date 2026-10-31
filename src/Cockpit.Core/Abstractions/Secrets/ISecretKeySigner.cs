namespace Cockpit.Core.Abstractions.Secrets;

/// <summary>
/// An OS-held key that can stand in for the password at unlock (AC-1879): Windows Hello on Windows. Every call
/// except <see cref="IsAvailableAsync"/> may show the OS prompt; a cancelled prompt is a false or a null, not a throw.
/// </summary>
public interface ISecretKeySigner
{
    /// <summary>Why the last call did not succeed, in words for the operator; null after a success.</summary>
    string? LastFailure { get; }

    /// <summary>Whether this machine can hold such a key at all (OS version, Hello set up, no remote session).</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates the key under <paramref name="name"/> (replacing one that exists) and signs <paramref name="challenge"/>
    /// with it under the one prompt of the creation. Null: cancelled or refused, with the reason in LastFailure.</summary>
    Task<SecretKeyEnrollment?> EnrollAsync(string name, byte[] challenge, CancellationToken cancellationToken = default);

    /// <summary>Whether the key under <paramref name="name"/> exists on this machine, without a prompt.</summary>
    Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Signs <paramref name="challenge"/> with the key; null when the key is gone, the prompt was cancelled, or it failed.</summary>
    Task<byte[]?> SignAsync(string name, byte[] challenge, CancellationToken cancellationToken = default);

    /// <summary>Removes the key; a key that is already gone is not an error.</summary>
    Task DeleteAsync(string name, CancellationToken cancellationToken = default);
}

// Where a created key lives: in the TPM, or (no TPM) in software.
public enum SecretKeyStorage
{
    Hardware,
    Software,
}

// A freshly created key and its signature over the challenge.
public sealed record SecretKeyEnrollment(SecretKeyStorage Storage, byte[] Signature);

// AC-1879: how an unlock with the OS key ended. `KeyGone` covers a PIN reset, a cleared TPM, another computer and a
// wrap older than the password: the OS key cannot open this configuration here, so only the password is offered.
public enum HelloUnlockResult
{
    NotOffered,
    Unlocked,
    Cancelled,
    KeyGone,

    // The first unlock found that this Windows signs differently each time (RSA-PSS); Hello has been turned off.
    Unsupported,
}
