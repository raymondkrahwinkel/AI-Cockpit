namespace Cockpit.Core.Abstractions.Secrets;

/// <summary>
/// An OS-held key that can stand in for the password at unlock (AC-1879): Windows Hello on Windows. Every call
/// except <see cref="IsAvailableAsync"/> may show the OS prompt; a cancelled prompt is a false or a null, not a throw.
/// </summary>
public interface ISecretKeySigner
{
    /// <summary>Whether this machine can hold such a key at all (OS version, Hello set up, no remote session).</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates the key under <paramref name="name"/>, replacing one that exists, and says where it is held.</summary>
    Task<SecretKeyStorage> CreateAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Whether the key under <paramref name="name"/> exists on this machine, without a prompt.</summary>
    Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Signs <paramref name="challenge"/> with the key; null when the key is gone, the prompt was cancelled, or it failed.</summary>
    Task<byte[]?> SignAsync(string name, byte[] challenge, CancellationToken cancellationToken = default);

    /// <summary>Removes the key; a key that is already gone is not an error.</summary>
    Task DeleteAsync(string name, CancellationToken cancellationToken = default);
}

// Where a created key lives; `None` means it was not created (cancelled or refused).
public enum SecretKeyStorage
{
    None,
    Hardware,
    Software,
}

// AC-1879: how an unlock with the OS key ended. `KeyGone` covers a PIN reset, a cleared TPM, another computer and a
// wrap older than the password: the OS key cannot open this configuration here, so only the password is offered.
public enum HelloUnlockResult
{
    NotOffered,
    Unlocked,
    Cancelled,
    KeyGone,
}
