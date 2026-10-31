namespace Cockpit.Core.Abstractions.Secrets;

/// <summary>
/// An OS-held key that can stand in for the password at unlock (AC-1879): Windows Hello on Windows. Every call
/// except <see cref="IsAvailableAsync"/> may show the OS prompt; a cancelled prompt is a false or a null, not a throw.
/// </summary>
public interface ISecretKeySigner
{
    /// <summary>Whether this machine can hold such a key at all (OS version, Hello set up, no remote session).</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates the key under <paramref name="name"/>, replacing one that exists. False: cancelled or refused.</summary>
    Task<bool> CreateAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Signs <paramref name="challenge"/> with the key; null when the key is gone, the prompt was cancelled, or it failed.</summary>
    Task<byte[]?> SignAsync(string name, byte[] challenge, CancellationToken cancellationToken = default);

    /// <summary>Removes the key; a key that is already gone is not an error.</summary>
    Task DeleteAsync(string name, CancellationToken cancellationToken = default);
}
