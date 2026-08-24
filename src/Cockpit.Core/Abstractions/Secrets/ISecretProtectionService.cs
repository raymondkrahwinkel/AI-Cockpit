namespace Cockpit.Core.Abstractions.Secrets;

// AC-1013 (AC-41): what the cockpit knows pre-unlock — `Enabled`, `Unlocked`, and `ShouldWarnUnprotected`
// (banner shows when encryption is off, a credential is in the clear, and it wasn't dismissed for this field
// set; defaults off so tests/stand-ins don't nag).
public readonly record struct SecretProtectionStatus(bool Enabled, bool Unlocked, bool ShouldWarnUnprotected = false);

// How far a migration has come, so the operator watches it happen instead of watching nothing happen.
// `Completed`/`Total`: fields converted so far / to convert.
public readonly record struct SecretMigrationProgress(int Completed, int Total);

/// <summary>
/// Turning credential encryption on and off, and unlocking it at startup. Every operation that rewrites the
/// config does so atomically and keeps a backup: a migration interrupted halfway — a crash, the power going —
/// must leave the operator with their credentials, not with half a file.
/// </summary>
public interface ISecretProtectionService
{
    Task<SecretProtectionStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Remembers that the operator dismissed the awareness banner (AC-41) for the credentials now in the file, so
    /// it does not nag again until a new credential is added. Bound to a fingerprint of the credential field paths
    /// — not their values — so rotating a key does not bring the banner back, but a new field does. A no-op once encryption is on, since there is then nothing to warn about.
    /// </summary>
    Task DismissUnprotectedWarningAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Derives the key from <paramref name="password"/> and, if it is the right one, unlocks the settings for this run. False means: wrong password.
    /// </summary>
    Task<bool> UnlockAsync(string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// Encrypts every credential in the settings with a key derived from <paramref name="password"/>, and leaves the app unlocked.
    /// </summary>
    Task EnableAsync(string password, IProgress<SecretMigrationProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes every credential back in the clear. Requires an unlocked app — there is no way to decrypt without the key.
    /// </summary>
    Task DisableAsync(IProgress<SecretMigrationProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-encrypts every credential under a new password (and a fresh salt).
    /// </summary>
    Task ChangePasswordAsync(string currentPassword, string newPassword, IProgress<SecretMigrationProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Empties every credential and turns encryption off: the way back in for an operator who forgot their
    /// password. Profiles, layout and shortcuts survive; tokens must be typed again. Without this a forgotten password would brick the app, and a promise with no way out is one people route around.
    /// </summary>
    Task ResetForgottenPasswordAsync(CancellationToken cancellationToken = default);
}
