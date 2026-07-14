namespace Cockpit.Core.Abstractions.Secrets;

/// <summary>What the cockpit knows about its own credential protection before anything is unlocked.</summary>
/// <param name="Enabled">Whether the operator turned encryption on.</param>
/// <param name="Unlocked">Whether the key for this session has been derived, so the settings can be read.</param>
public readonly record struct SecretProtectionStatus(bool Enabled, bool Unlocked);

/// <summary>How far a migration has come, so the operator watches it happen instead of watching nothing happen.</summary>
/// <param name="Completed">Fields converted so far.</param>
/// <param name="Total">Fields to convert.</param>
public readonly record struct SecretMigrationProgress(int Completed, int Total);

/// <summary>
/// Turning credential encryption on and off, and unlocking it at startup.
/// <para>
/// Every operation that rewrites the config does so atomically and keeps a backup: a migration interrupted
/// halfway — a crash, the power going — must leave the operator with their credentials, not with half a file.
/// </para>
/// </summary>
public interface ISecretProtectionService
{
    Task<SecretProtectionStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Derives the key from <paramref name="password"/> and, if it is the right one, unlocks the settings for this run. False means: wrong password.</summary>
    Task<bool> UnlockAsync(string password, CancellationToken cancellationToken = default);

    /// <summary>Encrypts every credential in the settings with a key derived from <paramref name="password"/>, and leaves the app unlocked.</summary>
    Task EnableAsync(string password, IProgress<SecretMigrationProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Writes every credential back in the clear. Requires an unlocked app — there is no way to decrypt without the key.</summary>
    Task DisableAsync(IProgress<SecretMigrationProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Re-encrypts every credential under a new password (and a fresh salt).</summary>
    Task ChangePasswordAsync(string currentPassword, string newPassword, IProgress<SecretMigrationProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Empties every credential and turns encryption off: the way back in for an operator who forgot their
    /// password. Their profiles, layout and shortcuts survive; the tokens have to be typed again. Without this
    /// a forgotten password would brick the app, and a promise with no way out is one people route around.
    /// </summary>
    Task ResetForgottenPasswordAsync(CancellationToken cancellationToken = default);
}
